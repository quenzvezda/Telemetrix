-- M8: review before applying to a local database. Safe to run repeatedly.
-- Scheduled retention removes old raw measurement chunks after 30 days and
-- rollup chunks after one year. Applying this file does not reset stored data.
CREATE MATERIALIZED VIEW IF NOT EXISTS measurement_1m
WITH (timescaledb.continuous) AS
SELECT time_bucket(INTERVAL '1 minute', time) AS bucket,
       tag_id,
       min(value) AS min_value,
       max(value) AS max_value,
       avg(value) AS avg_value,
       count(*) AS sample_count
FROM measurement
WHERE quality = 0
GROUP BY bucket, tag_id
WITH NO DATA;

SELECT add_continuous_aggregate_policy('measurement_1m',
    start_offset => INTERVAL '1 hour', end_offset => INTERVAL '1 minute',
    schedule_interval => INTERVAL '1 minute', if_not_exists => true);
SELECT add_retention_policy('measurement', INTERVAL '30 days', if_not_exists => true);
SELECT add_retention_policy('measurement_1m', INTERVAL '1 year', if_not_exists => true);

-- Populate retained history from earlier milestones, not only the refresh
-- policy's rolling one-hour window. Bound the refresh by the oldest raw bucket
-- still present so reruns do not erase older rollups after raw retention.
DO $$
DECLARE
    first_bucket timestamptz;
    last_bucket timestamptz := time_bucket(INTERVAL '1 minute', now() - INTERVAL '1 minute');
BEGIN
    SELECT time_bucket(INTERVAL '1 minute', min(time)) INTO first_bucket FROM measurement;
    IF first_bucket IS NOT NULL AND first_bucket < last_bucket THEN
        CALL refresh_continuous_aggregate('measurement_1m', first_bucket, last_bucket);
    END IF;
END $$;
