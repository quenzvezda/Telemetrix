"""Read-only queries over Telemetrix telemetry stored in TimescaleDB.

    from telemetrix_query import connect, Quality

    with connect() as client:  # TELEMETRIX_DATABASE_URL or PG* variables
        page = client.measurements("2026-10-01T00:00:00Z", "2026-10-02T00:00:00Z",
                                   asset_id="Pump01", qualities=Quality.BAD)
"""

from .client import READER_ROLE, TelemetrixClient, connect
from .errors import (ClientClosedError, ConnectionFailedError, FeatureUnavailableError, InsecureRoleWarning,
                     InvalidCursorError, PermissionDeniedError, QueryCancelledError, QueryTimeoutError,
                     SchemaMismatchError, TelemetrixQueryError)
from .models import Asset, Capabilities, Gap, Line, Measurement, Page, Plant, Quality, Rollup, Tag
from .settings import ENV_DATABASE_URL, ENV_QUERY_TIMEOUT, ConnectionSettings

__version__ = "0.1.0"

SUPPORTED_SCHEMA = "deploy/sql/init.sql with optional migrations 001-004"
"""The database schema this release reads; see README "Compatibility"."""

__all__ = [
    "Asset", "Capabilities", "ClientClosedError", "ConnectionFailedError", "ConnectionSettings",
    "ENV_DATABASE_URL", "ENV_QUERY_TIMEOUT", "FeatureUnavailableError", "Gap", "InsecureRoleWarning",
    "InvalidCursorError", "Line", "Measurement", "Page", "PermissionDeniedError", "Plant", "Quality",
    "QueryCancelledError", "QueryTimeoutError", "READER_ROLE", "Rollup", "SUPPORTED_SCHEMA",
    "SchemaMismatchError", "Tag", "TelemetrixClient", "TelemetrixQueryError", "__version__", "connect",
]
