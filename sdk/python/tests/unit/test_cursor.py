from datetime import datetime, timezone

import pytest

from telemetrix_query import InvalidCursorError
from telemetrix_query._cursor import decode, encode, fingerprint, parse_datetime, parse_int

T0 = datetime(2026, 10, 1, 10, 0, 0, 123456, tzinfo=timezone.utc)
PARSERS = (parse_datetime, parse_int)


def test_key_round_trips_with_microseconds():
    query = fingerprint("measurements", {"asset_id": "Pump01", "start": T0})

    assert decode(encode(query, (T0, 7)), query, PARSERS) == (T0, 7)


def test_cursor_from_another_query_is_rejected():
    pump = fingerprint("measurements", {"asset_id": "Pump01", "start": T0})
    tank = fingerprint("measurements", {"asset_id": "Tank01", "start": T0})

    with pytest.raises(InvalidCursorError, match="different query"):
        decode(encode(pump, (T0, 7)), tank, PARSERS)


def test_query_kind_is_part_of_the_fingerprint():
    assert fingerprint("measurements", {"start": T0}) != fingerprint("rollups", {"start": T0})


@pytest.mark.parametrize("cursor", ["", "not base64 at all!", "eyJ2IjoxfQ", encode("q", ("x", 1))])
def test_malformed_cursors_are_rejected(cursor):
    with pytest.raises(InvalidCursorError):
        decode(cursor, "q", PARSERS)


def test_invalid_cursor_is_also_a_value_error():
    with pytest.raises(ValueError):
        decode("garbage", "q", PARSERS)
