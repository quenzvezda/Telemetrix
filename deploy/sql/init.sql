-- Telemetrix schema. Runs once, on the first start of an empty timescaledb volume.
-- To re-run: docker compose -f deploy/docker-compose.yml down -v  (drops all data)
-- Continuous aggregate (FR-31) and retention policies (FR-32) are added in milestone 8.

CREATE EXTENSION IF NOT EXISTS timescaledb;

CREATE TABLE asset (
  asset_id   text PRIMARY KEY,          -- 'Pump01'
  plant_id   text NOT NULL,
  line_id    text NOT NULL,
  asset_type text NOT NULL              -- 'pump', 'tank'
);

CREATE TABLE tag (
  tag_id     serial PRIMARY KEY,
  asset_id   text NOT NULL REFERENCES asset,
  name       text NOT NULL,             -- 'MotorTemperature'
  unit       text,
  min_valid  double precision,
  max_valid  double precision,
  UNIQUE (asset_id, name)
);

CREATE TABLE measurement (
  time        timestamptz NOT NULL,     -- OPC UA source timestamp, UTC
  tag_id      int NOT NULL REFERENCES tag,
  value       double precision,
  quality     smallint NOT NULL,        -- 0 good, 1 uncertain, 2 bad
  batch_id    uuid NOT NULL,
  ingested_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tag_id, time)                 -- makes replays idempotent (ADR-0003)
);
SELECT create_hypertable('measurement', by_range('time'));

-- Seed: the simulated pumping line (docs/plant-telemetry-pipeline.md, "Simulated plant").
INSERT INTO asset (asset_id, plant_id, line_id, asset_type) VALUES
  ('Pump01', 'Plant01', 'Line01', 'pump'),
  ('Tank01', 'Plant01', 'Line01', 'tank');

INSERT INTO tag (asset_id, name, unit, min_valid, max_valid) VALUES
  ('Pump01', 'Running',           NULL,   NULL, NULL),
  ('Pump01', 'FlowRate',          'm3/h', 0,    80),
  ('Pump01', 'DischargePressure', 'bar',  0,    8),
  ('Pump01', 'MotorTemperature',  'degC', -20,  120),
  ('Pump01', 'Vibration',         'mm/s', 0,    20),
  ('Tank01', 'Level',             '%',    0,    100),
  ('Tank01', 'Temperature',       'degC', -20,  80);
