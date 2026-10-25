-- M8: durable dashboard history. Review before applying to a local database.
CREATE TABLE IF NOT EXISTS plant_event (
    event_id uuid PRIMARY KEY,
    schema_version integer NOT NULL CHECK (schema_version = 1),
    event_type text NOT NULL CHECK (event_type IN ('ConnectionLost','ConnectionRestored','Gap')),
    plant_id text NOT NULL,
    line_id text NOT NULL,
    asset_id text NOT NULL,
    endpoint text NOT NULL,
    occurred_at timestamptz NOT NULL,
    gap_start timestamptz,
    gap_end timestamptz,
    reason text,
    ingested_at timestamptz NOT NULL DEFAULT now(),
    CHECK (gap_end IS NULL OR gap_start IS NULL OR gap_end >= gap_start)
);
CREATE INDEX IF NOT EXISTS plant_event_asset_time ON plant_event(asset_id,occurred_at DESC);
CREATE INDEX IF NOT EXISTS plant_event_gap_time ON plant_event(asset_id,gap_start,gap_end)
    WHERE event_type='Gap';
