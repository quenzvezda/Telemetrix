-- M10: Python transformations (ADR-0018). Review before applying to a local database.
-- Required only when the Transformer runs with Transformations__Enabled=true.

-- A derived Tag names the transformation label that produces it; raw Tags keep NULL.
ALTER TABLE tag ADD COLUMN IF NOT EXISTS derived_by text;

-- One durable outcome per Batch and label; the primary key makes the first result final.
CREATE TABLE IF NOT EXISTS transformation_run (
    batch_id uuid NOT NULL,
    label text NOT NULL,
    asset_id text NOT NULL,
    script_version text,
    status text NOT NULL CHECK (status IN ('succeeded','failed')),
    reason text,
    error text,
    input_count integer NOT NULL CHECK (input_count >= 0),
    unchanged_count integer NOT NULL CHECK (unchanged_count >= 0),
    modified_count integer NOT NULL CHECK (modified_count >= 0),
    flagged_count integer NOT NULL CHECK (flagged_count >= 0),
    rejected_count integer NOT NULL CHECK (rejected_count >= 0),
    output_count integer NOT NULL CHECK (output_count >= 0),
    duration_ms double precision NOT NULL,
    processed_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (batch_id, label),
    CHECK (input_count = unchanged_count + modified_count + flagged_count + rejected_count),
    CHECK ((status = 'succeeded') = (reason IS NULL))
);
CREATE INDEX IF NOT EXISTS transformation_run_label_time ON transformation_run(label, processed_at DESC);

-- Flagged and rejected input Samples; unchanged and modified ones are counted on the run.
CREATE TABLE IF NOT EXISTS sample_disposition (
    batch_id uuid NOT NULL,
    label text NOT NULL,
    sample_index integer NOT NULL,
    tag_id integer NOT NULL REFERENCES tag,
    time timestamptz NOT NULL,
    disposition text NOT NULL CHECK (disposition IN ('flagged','rejected')),
    reason text NOT NULL,
    PRIMARY KEY (batch_id, label, sample_index),
    FOREIGN KEY (batch_id, label) REFERENCES transformation_run
);
CREATE INDEX IF NOT EXISTS sample_disposition_tag_time ON sample_disposition(tag_id, time);
