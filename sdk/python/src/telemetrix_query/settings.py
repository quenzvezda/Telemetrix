"""Connection settings from explicit values or the environment (ADR-0021)."""

from __future__ import annotations

import math
import os
from collections.abc import Mapping
from dataclasses import dataclass, field

from psycopg import ProgrammingError
from psycopg.conninfo import conninfo_to_dict

ENV_DATABASE_URL = "TELEMETRIX_DATABASE_URL"
ENV_QUERY_TIMEOUT = "TELEMETRIX_QUERY_TIMEOUT"


@dataclass(frozen=True)
class ConnectionSettings:
    """Where and how to connect.

    `conninfo` is a libpq URI or key/value string. Anything it leaves out — including the
    password — comes from libpq's own sources: `PGHOST`, `PGUSER`, `PGPASSWORD`, `~/.pgpass`.
    """

    conninfo: str = field(default="", repr=False)
    password: str | None = field(default=None, repr=False)
    connect_timeout: float = 10.0
    """Seconds to wait for the connection; libpq rounds up to whole seconds (minimum 2)."""
    query_timeout: float = 30.0
    """Default statement timeout in seconds; each query may override it."""
    application_name: str = "telemetrix-query"

    def __post_init__(self) -> None:
        for name in ("connect_timeout", "query_timeout"):
            value = getattr(self, name)
            if not isinstance(value, (int, float)) or isinstance(value, bool) or not math.isfinite(value) or value <= 0:
                raise ValueError(f"{name} must be a positive number of seconds, got {value!r}")

    @classmethod
    def from_env(cls, environ: Mapping[str, str] | None = None, **overrides: object) -> ConnectionSettings:
        """Read `TELEMETRIX_DATABASE_URL` and `TELEMETRIX_QUERY_TIMEOUT`; keyword overrides win."""
        environ = os.environ if environ is None else environ
        values: dict[str, object] = {"conninfo": environ.get(ENV_DATABASE_URL, "")}
        timeout = environ.get(ENV_QUERY_TIMEOUT)
        if timeout:
            try:
                values["query_timeout"] = float(timeout)
            except ValueError:
                raise ValueError(f"{ENV_QUERY_TIMEOUT} must be a number of seconds, got {timeout!r}") from None
        values.update(overrides)
        return cls(**values)  # type: ignore[arg-type]

    def describe(self) -> str:
        """The connection target without any secret, for messages and repr."""
        try:
            params = conninfo_to_dict(self.conninfo)
        except ProgrammingError:
            return "<unparseable conninfo>"
        shown = {key: value for key, value in params.items() if key in ("host", "hostaddr", "port", "dbname", "user")}
        if not shown:
            return "<libpq defaults>"
        return " ".join(f"{key}={value}" for key, value in shown.items())

    def __repr__(self) -> str:
        return (f"ConnectionSettings({self.describe()!r}, connect_timeout={self.connect_timeout}, "
                f"query_timeout={self.query_timeout}, application_name={self.application_name!r})")
