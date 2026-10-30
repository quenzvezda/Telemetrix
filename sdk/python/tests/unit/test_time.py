from datetime import datetime, timedelta, timezone

import pytest

from telemetrix_query._time import interval

UTC = timezone.utc


def test_offset_input_is_converted_to_utc():
    start, end = interval(datetime(2026, 10, 1, 17, tzinfo=timezone(timedelta(hours=7))),
                          "2026-10-01T11:00:00+00:00")

    assert start == datetime(2026, 10, 1, 10, tzinfo=UTC)
    assert start.tzinfo is UTC and end.tzinfo is UTC


def test_z_suffix_strings_are_accepted():
    start, end = interval("2026-10-01T00:00:00Z", "2026-10-01T00:00:00.000001Z")

    assert end - start == timedelta(microseconds=1)


@pytest.mark.parametrize("value", [datetime(2026, 10, 1), "2026-10-01T00:00:00"])
def test_naive_values_are_rejected(value):
    with pytest.raises(ValueError, match="no time zone"):
        interval(value, datetime(2026, 10, 2, tzinfo=UTC))


def test_end_must_follow_start():
    with pytest.raises(ValueError, match="must be after start"):
        interval("2026-10-01T00:00:00Z", "2026-10-01T07:00:00+07:00")


def test_other_types_are_rejected():
    with pytest.raises(TypeError, match="start must be a datetime"):
        interval(1_700_000_000, "2026-10-01T00:00:00Z")


def test_malformed_strings_are_explained():
    with pytest.raises(ValueError, match="not an ISO-8601"):
        interval("yesterday", "2026-10-01T00:00:00Z")
