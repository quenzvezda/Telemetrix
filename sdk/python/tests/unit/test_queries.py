from datetime import datetime, timezone

import pytest

from telemetrix_query import Quality
from telemetrix_query import _queries as q

START = datetime(2026, 10, 1, tzinfo=timezone.utc)
END = datetime(2026, 10, 2, tzinfo=timezone.utc)


def test_filters_combine_with_and_and_stay_parameters():
    sel = q.selection(plant_id="Plant01", asset_id="Pump01'; DROP TABLE tag; --", tags=["FlowRate", "Vibration"])

    sql, params = q.measurements(sel, (2,), START, END, None, 100)

    assert "DROP TABLE" not in sql
    assert params["asset_id"] == "Pump01'; DROP TABLE tag; --"
    assert params["tags"] == ["FlowRate", "Vibration"]
    assert params["qualities"] == [2]
    assert params["limit"] == 101
    assert "m.time >= %(start)s AND m.time < %(end)s" in sql
    assert sql.count(" AND ") == 5
    assert sql.endswith("ORDER BY m.time, m.tag_id LIMIT %(limit)s")


def test_cursor_continues_after_the_last_key():
    sql, params = q.measurements(q.selection(), None, START, END, (START, 4), 10)

    assert "(m.time, m.tag_id) > (%(after_time)s, %(after_id)s)" in sql
    assert params["after_time"] == START and params["after_id"] == 4
    assert "quality" not in params


def test_rollups_select_by_bucket_start():
    sql, _ = q.rollups(q.selection(asset_id="Tank01"), START, END, None, 10)

    assert "r.bucket >= %(start)s AND r.bucket < %(end)s" in sql
    assert "FROM public.measurement_1m r" in sql


def test_gaps_overlap_the_interval_and_reject_tag_filters():
    sql, _ = q.gaps(q.selection(asset_id="Pump01"), START, END, None, 10)

    assert "e.gap_start < %(end)s AND e.gap_end > %(start)s" in sql
    with pytest.raises(ValueError, match="Gaps belong to an Asset"):
        q.gaps(q.selection(tags="FlowRate"), START, END, None, 10)


def test_tags_without_the_derived_column_report_null():
    sql, _ = q.tags(q.selection(asset_id="Pump01"), derived=False, has_derived_column=False)

    assert "NULL::text" in sql and "derived_by IS NULL" not in sql


@pytest.mark.parametrize("values, expected", [
    (Quality.BAD, (2,)), ("good", (0,)), ([Quality.BAD, "uncertain", 2], (1, 2)), (None, None)])
def test_qualities_accept_enum_int_and_names(values, expected):
    assert q.qualities(values) == expected


@pytest.mark.parametrize("values", ["excellent", 3, []])
def test_invalid_qualities_are_rejected(values):
    with pytest.raises(ValueError):
        q.qualities(values)


@pytest.mark.parametrize("value", [0, 100_001])
def test_limits_are_bounded(value):
    with pytest.raises(ValueError, match="between 1 and 100,000"):
        q.limit(value)


def test_empty_filters_are_rejected_instead_of_matching_nothing():
    with pytest.raises(ValueError, match="tags must not be empty"):
        q.selection(tags=[])
    with pytest.raises(ValueError, match="asset_id must not be empty"):
        q.selection(asset_id="")
    with pytest.raises(TypeError):
        q.selection(tag_ids=["7"])
