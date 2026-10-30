-- M11: read-only access for Query SDK consumers (ADR-0021). Review before applying to a local database.
-- Creates a NOLOGIN group role; consumer logins are created by the operator as members of it,
-- with a password set interactively (sdk/python/README.md). No secret belongs in this file.

DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'telemetrix_reader') THEN
        CREATE ROLE telemetrix_reader NOLOGIN;
    END IF;
END $$;

DO $$
BEGIN
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO telemetrix_reader', current_database());
END $$;

GRANT USAGE ON SCHEMA public TO telemetrix_reader;
-- ALL TABLES includes views, so the measurement_1m continuous aggregate is readable.
GRANT SELECT ON ALL TABLES IN SCHEMA public TO telemetrix_reader;
-- Tables created later by the role that applies migrations are readable as well.
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO telemetrix_reader;
