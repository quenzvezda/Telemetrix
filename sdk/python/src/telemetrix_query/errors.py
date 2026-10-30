"""Exceptions raised by the SDK. Messages say what to do and never contain a password."""


class TelemetrixQueryError(Exception):
    """Base class of every SDK error."""


class ConnectionFailedError(TelemetrixQueryError):
    """The database could not be reached, or the connection was lost; create a new client."""


class QueryTimeoutError(TelemetrixQueryError):
    """The statement ran longer than its timeout and was cancelled by the server."""


class QueryCancelledError(TelemetrixQueryError):
    """The statement was cancelled through `TelemetrixClient.cancel()`."""


class PermissionDeniedError(TelemetrixQueryError):
    """The login lacks SELECT on a telemetry table; it should be a member of telemetrix_reader."""


class SchemaMismatchError(TelemetrixQueryError):
    """The database does not have the schema this SDK version reads."""


class FeatureUnavailableError(SchemaMismatchError):
    """An optional table is missing because its migration has not been applied."""


class InvalidCursorError(TelemetrixQueryError, ValueError):
    """The cursor is malformed or belongs to a different query."""


class ClientClosedError(TelemetrixQueryError):
    """The client was closed."""


class InsecureRoleWarning(UserWarning):
    """The login can write telemetry; use a login in the telemetrix_reader role."""
