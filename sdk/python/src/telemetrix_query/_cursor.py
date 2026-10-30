"""Opaque keyset cursors bound to the query that produced them (ADR-0020)."""

from __future__ import annotations

import base64
import binascii
import hashlib
import json
from collections.abc import Callable, Mapping, Sequence
from datetime import datetime
from typing import Any
from uuid import UUID

from .errors import InvalidCursorError

VERSION = 1


def fingerprint(kind: str, filters: Mapping[str, Any]) -> str:
    canonical = json.dumps([kind, filters], sort_keys=True, default=_plain, separators=(",", ":"))
    return hashlib.sha256(canonical.encode()).hexdigest()[:24]


def encode(query: str, key: Sequence[Any]) -> str:
    payload = json.dumps({"v": VERSION, "q": query, "k": [_plain(part) for part in key]}, separators=(",", ":"))
    return base64.urlsafe_b64encode(payload.encode()).decode().rstrip("=")


def decode(cursor: str, query: str, parsers: Sequence[Callable[[Any], Any]]) -> tuple[Any, ...]:
    if not isinstance(cursor, str) or not cursor:
        raise InvalidCursorError("cursor must be a non-empty string returned as Page.next_cursor")
    try:
        payload = json.loads(base64.urlsafe_b64decode(cursor + "=" * (-len(cursor) % 4)))
        if payload["v"] != VERSION:
            raise InvalidCursorError(f"cursor version {payload['v']} is not supported; restart the query")
        if payload["q"] != query:
            raise InvalidCursorError("cursor belongs to a different query; pass the same interval and filters, "
                                     "or omit the cursor to start again")
        key = payload["k"]
        if len(key) != len(parsers):
            raise ValueError("key length")
        return tuple(parse(part) for parse, part in zip(parsers, key))
    except InvalidCursorError:
        raise
    except (ValueError, KeyError, TypeError, binascii.Error) as error:
        raise InvalidCursorError("cursor is malformed; pass Page.next_cursor unchanged") from error


def _plain(value: Any) -> Any:
    if isinstance(value, datetime):
        return value.isoformat()
    if isinstance(value, UUID):
        return str(value)
    if isinstance(value, (tuple, list)):
        return [_plain(part) for part in value]
    return value


def parse_datetime(value: Any) -> datetime:
    parsed = datetime.fromisoformat(value)
    if parsed.tzinfo is None:
        raise ValueError("naive cursor time")
    return parsed


def parse_int(value: Any) -> int:
    if not isinstance(value, int) or isinstance(value, bool):
        raise ValueError("cursor id")
    return value
