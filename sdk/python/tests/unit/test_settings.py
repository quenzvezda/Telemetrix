import pytest

from telemetrix_query import ConnectionSettings


def test_environment_supplies_conninfo_and_timeout():
    settings = ConnectionSettings.from_env({"TELEMETRIX_DATABASE_URL": "postgresql://analyst@db:5432/telemetry",
                                            "TELEMETRIX_QUERY_TIMEOUT": "12.5"})

    assert settings.conninfo == "postgresql://analyst@db:5432/telemetry"
    assert settings.query_timeout == 12.5


def test_empty_environment_leaves_everything_to_libpq():
    settings = ConnectionSettings.from_env({})

    assert settings.conninfo == ""
    assert settings.password is None
    assert "libpq defaults" in repr(settings)


def test_overrides_win_over_the_environment():
    settings = ConnectionSettings.from_env({"TELEMETRIX_QUERY_TIMEOUT": "5"}, query_timeout=60)

    assert settings.query_timeout == 60


def test_repr_never_shows_a_password():
    settings = ConnectionSettings(conninfo="postgresql://analyst:uri-secret@db/telemetry", password="field-secret")

    text = repr(settings)
    assert "uri-secret" not in text
    assert "field-secret" not in text
    assert "user=analyst" in text and "host=db" in text


@pytest.mark.parametrize("value", [0, -1, float("nan"), float("inf"), True])
def test_timeouts_must_be_positive_seconds(value):
    with pytest.raises(ValueError, match="query_timeout"):
        ConnectionSettings(query_timeout=value)


def test_unparseable_environment_timeout_is_explained():
    with pytest.raises(ValueError, match="TELEMETRIX_QUERY_TIMEOUT"):
        ConnectionSettings.from_env({"TELEMETRIX_QUERY_TIMEOUT": "soon"})
