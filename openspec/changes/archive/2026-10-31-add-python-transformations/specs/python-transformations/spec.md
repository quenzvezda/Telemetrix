## ADDED Requirements

### Requirement: Registered transformations selected by label
When scripting is enabled, the transformer SHALL load a registry from its transformation directory that maps stable labels (`^[a-z0-9][a-z0-9-]{0,62}$`) to Python scripts inside that directory, with entry points, parameters and declared output Tags. A Batch SHALL select a transformation only by label, never by path or executable (FR-27, ADR-0017).

#### Scenario: Two Assets select two scripts
- **WHEN** a Pump01 Batch is labeled `pump-cleaning` and a Tank01 Batch is labeled `celsius-to-fahrenheit`, both registered
- **THEN** each Batch is transformed by its own script and each `transformation_run` row records that label

#### Scenario: Invalid registry
- **WHEN** scripting is enabled and a registered script is missing, escapes the directory, does not compile or lacks its entry point
- **THEN** the transformer fails to start with an error naming the label and does not consume Batches

### Requirement: Script versions pinned at startup
Each script version SHALL be `sha256:` plus the SHA-256 of its bytes when the transformer starts and SHALL stay fixed until it restarts. A registry entry MAY pin the hash; a mismatch SHALL fail startup (FR-27, ADR-0016).

#### Scenario: Script changed on disk
- **WHEN** a script file is edited while the transformer runs
- **THEN** Batches keep using the version loaded at startup until the transformer restarts

#### Scenario: Pinned hash mismatch
- **WHEN** a registry entry pins a hash that differs from the script bytes
- **THEN** the transformer fails to start with an error naming the label

### Requirement: Compatible default for unlabeled Batches
Batches without a label SHALL be processed only by the built-in pipeline whether scripting is enabled or not, with no transformation record (FR-27, ADR-0017).

#### Scenario: Unlabeled Batch with scripting enabled
- **WHEN** a schema version 1 Batch arrives while scripting is enabled
- **THEN** its Measurements are stored exactly as without scripting and no `transformation_run` row is written

### Requirement: Disabled scripting keeps the built-in pipeline
With scripting disabled, labeled Batches SHALL be processed only by the built-in pipeline, the skip SHALL be logged and counted, and the transformation tables SHALL NOT be required (FR-27, ADR-0017).

#### Scenario: Scripting disabled
- **WHEN** a labeled Batch arrives while scripting is disabled on a database without migration 003
- **THEN** its raw Measurements are stored, the offset is committed and `telemetrix.transformation.skipped` with reason `disabled` increases

### Requirement: Transformation input contract
The transformer SHALL call the entry point as `transform(batch, context)` in a managed Python child process. `batch` SHALL hold the Batch identity and each known-Tag Sample in Batch order with index, Tag, value, UTC timestamps, raw status code, Quality after the range check, unit and range. `context` SHALL hold the contract version, label, script version, parameters and declared outputs (FR-27, ADR-0016).

#### Scenario: Script sees validated raw Samples
- **WHEN** a labeled Batch contains a Good `MotorTemperature` Sample of 500 with range -20…120 and an unknown Tag `Torque`
- **THEN** the script receives the `MotorTemperature` Sample with quality `bad`, unit and range, and does not receive `Torque`

#### Scenario: Script prints
- **WHEN** a script writes to standard output
- **THEN** the text is logged and the transformation result is still read correctly

### Requirement: Input dispositions
Every input Sample SHALL receive exactly one disposition: `rejected` or `flagged` when the script says so with a reason, otherwise `modified` when it feeds an output, otherwise `unchanged`. Flagged and rejected Samples SHALL be stored in `sample_disposition`; their raw Measurements SHALL stay unchanged (FR-27, ADR-0018).

#### Scenario: Outliers flagged
- **WHEN** the outlier example flags one spike in a Pump01 Batch
- **THEN** a `sample_disposition` row `flagged` with a reason exists for that Sample, its raw Measurement is unchanged and the run counts one flagged input

#### Scenario: Empty output
- **WHEN** a script returns `{}`
- **THEN** the run is recorded as succeeded with zero outputs and every input unchanged

### Requirement: Output validation
The transformer SHALL validate the script result before storing anything from it. Each output SHALL target a declared output Tag, carry a finite numeric value and the source timestamp of one of its listed inputs, and SHALL NOT use a rejected input. Any violation SHALL record `malformed-output` and store nothing from the script (FR-27, ADR-0018).

#### Scenario: Invalid numeric output
- **WHEN** a script returns NaN, infinity, null or a string as an output value
- **THEN** the run is recorded as failed with reason `malformed-output` and no derived Measurement from that Batch is stored

#### Scenario: Invented timestamp
- **WHEN** an output Sample's source timestamp matches none of its inputs
- **THEN** the run is recorded as failed with reason `malformed-output`

### Requirement: Raw preservation and derived Tags
Transformations SHALL NOT change, replace or drop raw Measurements. Outputs SHALL be stored as derived Measurements under derived Tags marked with the producing label and created from the declared unit and range. An output Tag that is a raw Tag, belongs to another label or has a different unit SHALL fail the run with `output-tag-conflict` (FR-28, NFR-01, ADR-0018).

#### Scenario: Celsius to Fahrenheit
- **WHEN** a Tank01 Batch with `Temperature` 25 degC (Good) is transformed by `celsius-to-fahrenheit`
- **THEN** the raw `Temperature` Measurement is 25 and a `TemperatureF` Measurement at the same source timestamp is 77 under unit `degF`

#### Scenario: Output names a raw Tag
- **WHEN** a registry declares the output `FlowRate`, which is a raw Tag, and the script emits it
- **THEN** the run is recorded with reason `output-tag-conflict` and the raw `FlowRate` Measurement is unchanged

### Requirement: Derived Quality
A derived Measurement's Quality SHALL be the worst of its inputs' Quality, Uncertain when an input is flagged, and the script's declared Quality; it SHALL be Bad when the value is outside the derived Tag's range. Scripts SHALL NOT raise Quality (FR-22, FR-28).

#### Scenario: Converted value out of the derived range
- **WHEN** a converted value lies outside the derived Tag's range
- **THEN** the derived Measurement is stored with quality Bad and the raw Measurement keeps its own Quality

#### Scenario: Script cannot upgrade Quality
- **WHEN** a script declares quality `good` for an output whose input is Uncertain
- **THEN** the derived Measurement is stored with quality Uncertain

### Requirement: Atomic transformation record
The raw Measurements, derived Measurements, dispositions and one `transformation_run` row per Batch and label SHALL be written in one transaction before the offset is committed (FR-24, FR-28, NFR-03).

#### Scenario: Successful run
- **WHEN** a labeled Batch is transformed successfully
- **THEN** its run row has status `succeeded`, the script version and input, disposition and output counts, and the offset is committed after the transaction

### Requirement: Permanent failures are recorded
Unknown labels, script exceptions, timeouts, worker crashes, malformed output and Tag conflicts SHALL be recorded as failed runs with a reason and error while the raw Measurements are stored and the offset is committed (FR-28, NFR-01).

#### Scenario: Script exception
- **WHEN** the selected script raises an exception
- **THEN** the raw Measurements are stored, the run has status `failed`, reason `script-error` and the error text, and the next Batch is processed

#### Scenario: Timeout
- **WHEN** a script runs longer than the configured timeout
- **THEN** the worker is killed, the run is recorded with reason `timeout`, and the next Batch is transformed by a restarted worker

#### Scenario: Unknown label
- **WHEN** scripting is enabled and a Batch is labeled with an unregistered label
- **THEN** its raw Measurements are stored and the run is recorded with reason `unknown-label`

### Requirement: Unavailable runtime holds the offset
When the Python worker cannot be started, the transformer SHALL NOT commit the Batch and SHALL retry it with backoff (FR-24, ADR-0016).

#### Scenario: Interpreter missing during operation
- **WHEN** the worker cannot be restarted for a labeled Batch
- **THEN** that Batch's offset is not committed and processing resumes once the worker starts

### Requirement: Idempotent replay of transformed Batches
A Batch with an existing run row SHALL NOT be transformed again, so replays and script-version changes neither duplicate nor mix derived results; per-Asset order and source timestamps SHALL be preserved (NFR-01, NFR-03, NFR-05).

#### Scenario: Replay after a script change
- **WHEN** a transformed Batch is consumed again after the transformer restarts with a changed script
- **THEN** the Measurement count, derived values and run row, including its script version, are unchanged

### Requirement: Transformation observability
The transformer SHALL expose per-label run counts and duration by script version and outcome, input Samples by disposition, output Samples and skipped Batches by reason, and SHALL log each run with the Batch id, label, script version, outcome, counts and duration (FR-41, NFR-12).

#### Scenario: Metrics after two runs
- **WHEN** one Batch is transformed successfully and one fails with `script-error`
- **THEN** `telemetrix.transformation.runs` shows one `succeeded` and one `script-error` outcome and the duration histogram has two observations
