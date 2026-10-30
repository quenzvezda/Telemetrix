"""A disposable TimescaleDB with the real schema, migrations and a reader login."""

from __future__ import annotations

import io
import secrets
import tarfile
import time
from collections.abc import Iterator
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path

import psycopg
import pytest
from psycopg import sql
from testcontainers.core.container import DockerContainer

from telemetrix_query import ConnectionSettings

REPO = Path(__file__).resolve().parents[4]
SQL = REPO / "deploy" / "sql"
IMAGE = "timescale/timescaledb:2.30.2-pg17"
UTC = timezone.utc

# Seeded instants; each test family owns its own interval so tests stay independent.
BOUNDARY = datetime(2026, 10, 1, 10, 0, tzinfo=UTC)   # MotorTemperature: Good, Bad, null, Uncertain, Good at +60 s
ROLLUP_MINUTE = datetime(2026, 10, 1, 11, 0, tzinfo=UTC)  # FlowRate: Good 10/20/30, Bad 999
LARGE = datetime(2026, 10, 2, tzinfo=UTC)              # Tank01 Level: 25,000 Good, one per second
LARGE_COUNT = 25_000
GAP_DAY = datetime(2026, 10, 3, tzinfo=UTC)            # plant events
PAGING = datetime(2026, 10, 4, tzinfo=UTC)             # Vibration rows for concurrent-insert paging


@dataclass(frozen=True)
class Database:
    host: str
    port: int
    admin_password: str
    reader_password: str
    outsider_password: str

    def conninfo(self, user: str, dbname: str = "telemetry") -> str:
        return f"host={self.host} port={self.port} dbname={dbname} user={user}"

    def settings(self, user: str = "sdk_reader", dbname: str = "telemetry", **overrides) -> ConnectionSettings:
        password = {"sdk_reader": self.reader_password, "sdk_outsider": self.outsider_password,
                    "postgres": self.admin_password}[user]
        return ConnectionSettings(conninfo=self.conninfo(user, dbname), password=password, **overrides)

    def admin(self, dbname: str = "telemetry") -> psycopg.Connection:
        return psycopg.connect(self.conninfo("postgres", dbname), password=self.admin_password, autocommit=True)


@pytest.fixture(scope="session")
def database() -> Iterator[Database]:
    admin_password = secrets.token_urlsafe(16)
    container = (DockerContainer(IMAGE)
                 .with_env("POSTGRES_PASSWORD", admin_password)
                 .with_env("POSTGRES_DB", "telemetry")
                 .with_exposed_ports(5432))
    container.start()
    try:
        db = Database(container.get_container_host_ip(), int(container.get_exposed_port(5432)), admin_password,
                      secrets.token_urlsafe(16), secrets.token_urlsafe(16))
        _wait_for_tcp(db)
        _copy_sql(container)
        _psql(container, "telemetry", "-f", "/sql/init.sql")
        _exec(container, ["/bin/sh", "-c", "PGUSER=postgres PGDATABASE=telemetry /bin/sh /migrate.sh"])
        with db.admin() as admin:
            admin.execute("CREATE DATABASE bare")
            admin.execute("CREATE DATABASE foreign_db")
        _psql(container, "bare", "-f", "/sql/init.sql")
        _create_logins(db)
        _seed(db)
        yield db
    finally:
        container.stop()


@pytest.fixture
def admin(database: Database) -> Iterator[psycopg.Connection]:
    with database.admin() as connection:
        yield connection


def _wait_for_tcp(db: Database) -> None:
    # The image initialises on a Unix socket only, so a TCP connection means the final server is up.
    deadline = time.monotonic() + 90
    while True:
        try:
            with db.admin("postgres") as connection:
                connection.execute("SELECT 1")
            return
        except psycopg.OperationalError:
            if time.monotonic() > deadline:
                raise
            time.sleep(0.5)


def _copy_sql(container: DockerContainer) -> None:
    archive = io.BytesIO()
    with tarfile.open(fileobj=archive, mode="w") as tar:
        tar.add(SQL / "init.sql", arcname="sql/init.sql")
        tar.add(SQL / "migrate.sh", arcname="migrate.sh")
        for migration in sorted((SQL / "migrations").glob("*.sql")):
            tar.add(migration, arcname=f"migrations/{migration.name}")
    container.get_wrapped_container().put_archive("/", archive.getvalue())


def _psql(container: DockerContainer, dbname: str, *args: str) -> None:
    _exec(container, ["psql", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", dbname, *args])


def _exec(container: DockerContainer, command: list[str]) -> None:
    result = container.exec(command)
    assert result.exit_code == 0, result.output.decode(errors="replace")


def _create_logins(db: Database) -> None:
    with db.admin() as admin:
        admin.execute(sql.SQL("CREATE ROLE sdk_reader LOGIN PASSWORD {} IN ROLE telemetrix_reader")
                      .format(sql.Literal(db.reader_password)))
        admin.execute(sql.SQL("CREATE ROLE sdk_outsider LOGIN PASSWORD {}").format(sql.Literal(db.outsider_password)))


def _seed(db: Database) -> None:
    with db.admin() as admin:
        def insert(tag_id: int, at: datetime, value: float | None, quality: int) -> None:
            admin.execute("INSERT INTO measurement(time, tag_id, value, quality, batch_id) "
                          "VALUES (%s, %s, %s, %s, '11111111-1111-1111-1111-111111111111')",
                          (at, tag_id, value, quality))

        # MotorTemperature (tag 4, range -20..120): Good, Bad out of range, null Bad, Uncertain, and one at +60 s.
        for offset, value, quality in ((0, 50.0, 0), (10, 150.0, 2), (20, None, 2), (30, 60.0, 1), (60, 55.0, 0)):
            insert(4, BOUNDARY + timedelta(seconds=offset), value, quality)
        # FlowRate (tag 2): three Good values and one Bad value in one minute.
        for offset, value, quality in ((5, 10.0, 0), (15, 20.0, 0), (25, 30.0, 0), (35, 999.0, 2)):
            insert(2, ROLLUP_MINUTE + timedelta(seconds=offset), value, quality)
        # Tank01 Level (tag 6): a large interval.
        admin.execute("INSERT INTO measurement(time, tag_id, value, quality, batch_id) "
                      "SELECT %s + make_interval(secs => n), 6, n %% 100, 0, gen_random_uuid() "
                      "FROM generate_series(0, %s - 1) AS n", (LARGE, LARGE_COUNT))
        # Vibration (tag 5): ten rows for paging while inserts arrive.
        for second in range(0, 20, 2):
            insert(5, PAGING + timedelta(seconds=second), float(second), 0)
        # A derived Tag produced by a transformation (migration 003).
        derived_id = admin.execute(
            "INSERT INTO tag(asset_id, name, unit, min_valid, max_valid, derived_by) "
            "VALUES ('Tank01', 'TemperatureF', 'degF', -4, 176, 'celsius-to-fahrenheit') RETURNING tag_id").fetchone()[0]
        insert(derived_id, BOUNDARY, 77.0, 0)
        # Gaps and a non-Gap plant event (migration 002).
        for event_id, event_type, asset, start, end in (
                ("00000000-0000-0000-0000-00000000000a", "Gap", "Pump01", GAP_DAY.replace(hour=10),
                 GAP_DAY.replace(hour=10, minute=5)),
                ("00000000-0000-0000-0000-00000000000b", "Gap", "Tank01", GAP_DAY.replace(hour=12),
                 GAP_DAY.replace(hour=12, minute=1)),
                ("00000000-0000-0000-0000-00000000000c", "ConnectionLost", "Pump01", None, None)):
            admin.execute(
                "INSERT INTO plant_event(event_id, schema_version, event_type, plant_id, line_id, asset_id, endpoint, "
                "occurred_at, gap_start, gap_end, reason) VALUES (%s, 1, %s, 'Plant01', 'Line01', %s, "
                "'opc.tcp://plantsim:4840', %s, %s, %s, 'source-disconnected')",
                (event_id, event_type, asset, end or GAP_DAY.replace(hour=9), start, end))
        admin.execute("CALL refresh_continuous_aggregate('measurement_1m', NULL, NULL)")
