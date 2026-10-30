import threading
import warnings

import psycopg
import pytest

from telemetrix_query import (ClientClosedError, ConnectionFailedError, ConnectionSettings, InsecureRoleWarning,
                              PermissionDeniedError, QueryCancelledError, QueryTimeoutError, SchemaMismatchError,
                              connect)

pytestmark = pytest.mark.integration


def test_reader_login_connects_without_a_warning(database):
    with warnings.catch_warnings():
        warnings.simplefilter("error")
        with connect(database.settings()) as client:
            assert client.capabilities.can_write is False


def test_owner_login_warns_and_the_session_stays_read_only(database):
    with pytest.warns(InsecureRoleWarning, match="telemetrix_reader"):
        client = connect(database.settings("postgres"))
    with client:
        with pytest.raises(psycopg.errors.ReadOnlySqlTransaction):
            client._connection.execute("CREATE TABLE written_through_sdk(id int)")


def test_timeout_raises_and_the_client_stays_usable(database, admin):
    with connect(database.settings()) as client:
        admin.execute("BEGIN")
        admin.execute("LOCK TABLE tag IN ACCESS EXCLUSIVE MODE")
        try:
            with pytest.raises(QueryTimeoutError, match="0.5 s"):
                client.tags(timeout=0.5)
        finally:
            admin.execute("ROLLBACK")
        assert len(client.tags(asset_id="Tank01")) >= 2


def test_cancel_from_another_thread(database, admin):
    with connect(database.settings()) as client:
        admin.execute("BEGIN")
        admin.execute("LOCK TABLE tag IN ACCESS EXCLUSIVE MODE")
        timer = threading.Timer(0.5, client.cancel)
        timer.start()
        try:
            with pytest.raises(QueryCancelledError):
                client.tags(timeout=30)
        finally:
            timer.join()
            admin.execute("ROLLBACK")
        assert client.plants()


def test_login_without_the_reader_role_is_told_what_to_grant(database):
    with connect(database.settings("sdk_outsider")) as client:
        with pytest.raises(PermissionDeniedError, match="GRANT telemetrix_reader TO"):
            client.tags()


def test_unreachable_database_never_echoes_the_password(database):
    password = "never-show-this-password"
    settings = ConnectionSettings(conninfo=f"host={database.host} port=1 dbname=telemetry user=sdk_reader",
                                  password=password, connect_timeout=2)

    with pytest.raises(ConnectionFailedError) as raised:
        connect(settings)

    assert password not in str(raised.value)
    assert "port=1" in str(raised.value)


def test_wrong_password_is_a_connection_failure(database):
    settings = ConnectionSettings(conninfo=database.conninfo("sdk_reader"), password="wrong-password")

    with pytest.raises(ConnectionFailedError) as raised:
        connect(settings)

    assert "wrong-password" not in str(raised.value)


def test_database_without_telemetrix_tables(database):
    with pytest.raises(SchemaMismatchError, match="asset, tag, measurement"):
        connect(database.settings("postgres", dbname="foreign_db"))


def test_closed_client(database):
    client = connect(database.settings())
    client.close()

    with pytest.raises(ClientClosedError):
        client.plants()


def test_environment_configuration(database, monkeypatch):
    monkeypatch.setenv("TELEMETRIX_DATABASE_URL",
                       f"postgresql://sdk_reader@{database.host}:{database.port}/telemetry")
    monkeypatch.setenv("PGPASSWORD", database.reader_password)
    monkeypatch.setenv("TELEMETRIX_QUERY_TIMEOUT", "7")

    with connect() as client:
        assert client.settings.query_timeout == 7
        assert [p.plant_id for p in client.plants()] == ["Plant01"]
