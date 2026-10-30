from datetime import datetime, timedelta, timezone

import pytest

from telemetrix_query import FeatureUnavailableError, InsecureRoleWarning, InvalidCursorError, Quality, connect

from .conftest import BOUNDARY, GAP_DAY, LARGE, LARGE_COUNT, PAGING, ROLLUP_MINUTE

pytestmark = pytest.mark.integration
UTC = timezone.utc


@pytest.fixture
def client(database):
    with connect(database.settings()) as client:
        yield client


def test_hierarchy_and_tag_metadata(client):
    assert [p.plant_id for p in client.plants()] == ["Plant01"]
    assert [(line.plant_id, line.line_id) for line in client.lines(plant_id="Plant01")] == [("Plant01", "Line01")]
    assert [a.asset_id for a in client.assets(asset_type="pump")] == ["Pump01"]

    tags = client.tags(asset_id="Pump01")

    assert [t.name for t in tags] == ["DischargePressure", "FlowRate", "MotorTemperature", "Running", "Vibration"]
    motor = next(t for t in tags if t.name == "MotorTemperature")
    assert (motor.unit, motor.min_valid, motor.max_valid, motor.plant_id, motor.line_id) == \
        ("degC", -20, 120, "Plant01", "Line01")
    assert all(t.derived_by is None and not t.is_derived for t in tags)


def test_derived_tags_name_their_transformation(client):
    derived = client.tags(derived=True)

    assert [(t.asset_id, t.name, t.unit, t.derived_by) for t in derived] == \
        [("Tank01", "TemperatureF", "degF", "celsius-to-fahrenheit")]
    assert "TemperatureF" not in {t.name for t in client.tags(derived=False)}


def test_measurements_for_an_asset_interval_are_typed_and_half_open(client):
    page = client.measurements(BOUNDARY, BOUNDARY + timedelta(seconds=60), asset_id="Pump01", tags="MotorTemperature")

    assert [(m.time - BOUNDARY).seconds for m in page] == [0, 10, 20, 30]  # +60 s is the excluded end
    first = page.items[0]
    assert (first.asset_id, first.tag, first.unit, first.value, first.quality) == \
        ("Pump01", "MotorTemperature", "degC", 50.0, Quality.GOOD)
    assert first.time.tzinfo is UTC and first.ingested_at.tzinfo is UTC
    assert str(first.batch_id) == "11111111-1111-1111-1111-111111111111"
    assert not page.has_more


def test_null_bad_and_absent_are_distinct(client):
    page = client.measurements(BOUNDARY, BOUNDARY + timedelta(seconds=25), tags="MotorTemperature", asset_id="Pump01")

    assert [(m.value, m.quality) for m in page] == [(50.0, Quality.GOOD), (150.0, Quality.BAD), (None, Quality.BAD)]
    empty = client.measurements(BOUNDARY - timedelta(hours=1), BOUNDARY, asset_id="Pump01")
    assert empty.items == [] and empty.next_cursor is None


def test_quality_filter(client):
    bad = client.measurements(BOUNDARY, BOUNDARY + timedelta(minutes=1), asset_id="Pump01", qualities=Quality.BAD)
    not_good = client.measurements(BOUNDARY, BOUNDARY + timedelta(minutes=1), asset_id="Pump01",
                                   qualities=["uncertain", "bad"])

    assert [m.value for m in bad] == [150.0, None]
    assert [m.quality for m in not_good] == [Quality.BAD, Quality.BAD, Quality.UNCERTAIN]


def test_offsets_select_the_same_instants(client):
    jakarta = timezone(timedelta(hours=7))
    utc_page = client.measurements(BOUNDARY, BOUNDARY + timedelta(minutes=2), asset_id="Pump01")
    local_page = client.measurements(BOUNDARY.astimezone(jakarta), "2026-10-01T17:02:00+07:00", asset_id="Pump01")

    assert utc_page.items == local_page.items
    assert len(utc_page) == 5


def test_hierarchy_filters_and_tag_ids_combine(client):
    tank = client.measurements(BOUNDARY, BOUNDARY + timedelta(minutes=1), plant_id="Plant01", line_id="Line01",
                               asset_id="Tank01")
    by_id = client.measurements(BOUNDARY, BOUNDARY + timedelta(minutes=1), tag_ids=[4], qualities="good")

    assert [m.tag for m in tank] == ["TemperatureF"]
    assert [m.value for m in by_id] == [50.0]


def test_large_interval_streams_once_each_in_order(client):
    seen = 0
    previous = None
    for measurement in client.iter_measurements(LARGE, LARGE + timedelta(days=1), asset_id="Tank01", tags="Level",
                                                page_size=1_000):
        assert previous is None or measurement.time > previous
        previous = measurement.time
        seen += 1

    assert seen == LARGE_COUNT
    assert previous == LARGE + timedelta(seconds=LARGE_COUNT - 1)


def test_pages_cover_every_row_without_overlap(client):
    end = LARGE + timedelta(seconds=2_500)
    first = client.measurements(LARGE, end, tags="Level", limit=1_000)
    second = client.measurements(LARGE, end, tags="Level", limit=1_000, cursor=first.next_cursor)
    third = client.measurements(LARGE, end, tags="Level", limit=1_000, cursor=second.next_cursor)

    times = [m.time for page in (first, second, third) for m in page]
    assert len(times) == len(set(times)) == 2_500
    assert third.next_cursor is None


def test_concurrent_inserts_do_not_shift_pages(client, admin):
    def insert(second: int) -> None:
        admin.execute("INSERT INTO measurement(time, tag_id, value, quality, batch_id) "
                      "VALUES (%s, 5, 0, 0, gen_random_uuid())", (PAGING + timedelta(seconds=second),))

    end = PAGING + timedelta(minutes=1)
    first = client.measurements(PAGING, end, tags="Vibration", limit=4)
    insert(30)  # later: appears on a later page
    insert(1)   # before the cursor: only a new query sees it
    rest = list(client.iter_measurements(PAGING, end, tags="Vibration", page_size=100))
    second = client.measurements(PAGING, end, tags="Vibration", limit=4, cursor=first.next_cursor)

    assert [(m.time - PAGING).seconds for m in first] == [0, 2, 4, 6]
    assert [(m.time - PAGING).seconds for m in second] == [8, 10, 12, 14]
    assert (rest[1].time - PAGING).seconds == 1 and (rest[-1].time - PAGING).seconds == 30


def test_cursor_for_another_query_is_rejected(client):
    page = client.measurements(LARGE, LARGE + timedelta(hours=1), asset_id="Tank01", limit=10)

    with pytest.raises(InvalidCursorError):
        client.measurements(LARGE, LARGE + timedelta(hours=1), asset_id="Pump01", limit=10, cursor=page.next_cursor)


def test_rollups_hold_good_measurements_only(client):
    page = client.rollups(ROLLUP_MINUTE, ROLLUP_MINUTE + timedelta(minutes=1), asset_id="Pump01", tags="FlowRate")

    [rollup] = page.items
    assert (rollup.bucket, rollup.tag, rollup.unit) == (ROLLUP_MINUTE, "FlowRate", "m3/h")
    assert (rollup.min_value, rollup.max_value, rollup.avg_value, rollup.sample_count) == (10, 30, 20, 3)


def test_rollups_stream_by_bucket(client):
    rollups = list(client.iter_rollups(LARGE, LARGE + timedelta(minutes=10), tags="Level", page_size=3))

    assert [r.bucket for r in rollups] == [LARGE + timedelta(minutes=i) for i in range(10)]
    assert all(r.sample_count == 60 for r in rollups)


def test_gaps_overlap_the_interval(client):
    page = client.gaps(GAP_DAY.replace(hour=10, minute=4), GAP_DAY.replace(hour=11), asset_id="Pump01")

    [gap] = page.items
    assert (gap.start, gap.end, gap.reason, gap.asset_id) == \
        (GAP_DAY.replace(hour=10), GAP_DAY.replace(hour=10, minute=5), "source-disconnected", "Pump01")
    assert [g.asset_id for g in client.iter_gaps(GAP_DAY, GAP_DAY + timedelta(days=1), page_size=1)] == \
        ["Pump01", "Tank01"]
    assert client.gaps(GAP_DAY.replace(hour=10, minute=5), GAP_DAY.replace(hour=11), asset_id="Pump01").items == []


def test_initial_schema_only(database):
    with pytest.warns(InsecureRoleWarning):  # the bare database has no reader role
        client = connect(database.settings("postgres", dbname="bare"))
    with client:
        assert (client.capabilities.rollups, client.capabilities.gaps, client.capabilities.derived_tags) == \
            (False, False, False)
        assert len(client.tags(asset_id="Pump01")) == 5
        assert client.tags(derived=True) == []
        assert client.measurements(BOUNDARY, BOUNDARY + timedelta(days=1)).items == []
        with pytest.raises(FeatureUnavailableError, match="001_rollups_and_retention.sql"):
            client.rollups(BOUNDARY, BOUNDARY + timedelta(days=1))
        with pytest.raises(FeatureUnavailableError, match="002_plant_events.sql"):
            client.gaps(BOUNDARY, BOUNDARY + timedelta(days=1))


def test_reader_capabilities(client):
    caps = client.capabilities

    assert (caps.rollups, caps.gaps, caps.derived_tags, caps.can_write) == (True, True, True, False)
    assert caps.server_version.startswith("17") and caps.timescaledb_version == "2.30.2"


def test_naive_interval_is_rejected_before_querying(client):
    with pytest.raises(ValueError, match="no time zone"):
        client.measurements(datetime(2026, 10, 1), datetime(2026, 10, 2))
