## ADDED Requirements

### Requirement: Health endpoint
Bridge, Transformer, PlantSim and MqttSim SHALL each expose an HTTP `/health` endpoint reflecting critical dependency and worker readiness (FR-41). Healthy and Degraded results SHALL return HTTP 200; failed critical checks SHALL return HTTP 503 identifying the failing check. Bridge SHALL report Degraded while dependencies are ready but its buffer is non-empty.

#### Scenario: All dependencies up
- **WHEN** `/health` is requested while the service's dependencies and worker are ready and, for Bridge, its buffer is empty
- **THEN** it responds 200 with status Healthy

#### Scenario: Bridge source dependency
- **WHEN** Bridge's configured OPC UA or MQTT source connection is unavailable
- **THEN** its `/health` responds 503 identifying the source check until the configured source reconnects

#### Scenario: Transformer dependencies
- **WHEN** Transformer cannot reach Kafka or its database `SELECT 1` check fails
- **THEN** its `/health` responds 503 identifying the failed dependency

#### Scenario: Dependency down
- **WHEN** Kafka is stopped and the bridge's `/health` is requested
- **THEN** it responds with a non-200 status naming Kafka as the failing check, while the bridge keeps collecting into its buffer

#### Scenario: Buffer drains with dependencies healthy
- **WHEN** Bridge's source and Kafka are reachable and its store-and-forward buffer still contains accepted batches
- **THEN** `/health` responds 200 with status Degraded, returning Healthy once the buffer is empty

#### Scenario: PlantSim is ready
- **WHEN** PlantSim's OPC UA server is started and its simulation loop is running
- **THEN** its `/health` responds 200 with status Healthy; a failed server or simulation-loop check instead returns 503

#### Scenario: MqttSim broker recovery
- **WHEN** MqttSim loses its broker connection and subsequently reconnects and resumes publication
- **THEN** its `/health` changes from 503 naming the broker connection to 200 with status Healthy

### Requirement: Pipeline metrics
The services SHALL export OpenTelemetry metrics for samples received, batches produced, store-and-forward buffer depth, consumer lag and rows written, each labelled with the service name and, where applicable, the asset (FR-41).

#### Scenario: Metrics reflect flow
- **WHEN** the pipeline runs at 70 samples/s for one minute
- **THEN** the samples-received and rows-written counters each increase by about 4,200 and the buffer-depth gauge stays at 0

#### Scenario: Buffer depth rises during an outage
- **WHEN** Kafka is unreachable for one minute while the bridge collects
- **THEN** the buffer-depth gauge rises during the outage and returns to 0 after Kafka is back and the buffer drains

#### Scenario: Consumer lag is visible
- **WHEN** consumption is paused while the Transformer process and metrics endpoint stay alive and Bridge keeps producing
- **THEN** the consumer-lag metric for group `transformer` grows and falls as consumption resumes and the backlog drains

### Requirement: Correlated structured logs
Bridge, Transformer, PlantSim and MqttSim SHALL write structured JSON logs to the console, and every log entry about a batch in Bridge or Transformer SHALL include its `batchId` as a searchable correlation property (NFR-12).

#### Scenario: Trace one batch end to end
- **WHEN** the logs of the bridge and transformer are filtered by one `batchId`
- **THEN** they show the batch being produced by the bridge and then consumed and written by the transformer

### Requirement: Latency and throughput targets
The pipeline SHALL keep the 95th percentile of `ingested_at - time` under 10 seconds at default batch settings (NFR-06), and SHALL sustain 5,000 samples/s on a developer laptop without consumer lag growing over a 15-minute run (NFR-07). The M8 baseline SHALL be 500 samples or 5 seconds per asset, whichever comes first.

#### Scenario: Latency at default settings
- **WHEN** the stack runs at the default simulation rate for 30 minutes with the 500 samples / 5 seconds baseline
- **THEN** the p95 of `ingested_at - time` over that period is below 10 seconds

#### Scenario: Sustained throughput
- **WHEN** the simulator is scaled to 5,000 samples/s for 15 minutes with the 500 samples / 5 seconds baseline
- **THEN** consumer lag for group `transformer` shows no upward trend and no batch is dead-lettered

### Requirement: Evidence-based batch threshold decision

D5 SHALL remain open until the baseline and at least one alternative are compared on the same machine and load profiles (NFR-06, NFR-07). The reviewer SHALL select final defaults from that evidence and the outcome SHALL be recorded in an ADR before M8 acceptance. Changed defaults SHALL be reflected in the batching requirement and verified by repeated acceptance runs.

#### Scenario: D5 is decided after measurement
- **WHEN** the baseline and at least one alternative have completed the same 30-minute latency and 15-minute throughput profiles
- **THEN** the comparison records settings, machine, run windows, p95, actual throughput, lag and dead-letter counts for reviewer selection, and D5 is resolved only after the reviewer accepts a choice and its ADR is recorded

#### Scenario: Selected defaults differ from the baseline
- **WHEN** the reviewer selects thresholds other than 500 samples / 5 seconds
- **THEN** the batching requirement and configuration are aligned with that decision and the selected defaults pass the NFR-06 / NFR-07 acceptance runs before M8 is accepted

### Requirement: Reproducible load profile
The load tooling SHALL retain normal simulator defaults, scale uniquely identified pumping lines for the throughput profile, and record the applied source, batching and queue settings (NFR-06, NFR-07). Throughput evidence SHALL compare source acknowledgments, received samples and final stored measurements, and SHALL fail if acknowledged samples do not drain to storage.

#### Scenario: Default simulator remains compatible
- **WHEN** MqttSim starts without load overrides
- **THEN** it publishes the original seven tag topics at 1 Hz with one in-flight publication

#### Scenario: Scaled source preserves identity and order
- **WHEN** the throughput profile runs 143 pumping-line clones at 5 Hz
- **THEN** every clone has distinct asset/tag identities, every source tick completes before the next starts, and the report includes actual publication and reception rates and a final drain count

#### Scenario: Bounded batch queue tuning
- **WHEN** a load profile configures `Ingestion:BatchQueueCapacity` between 1 and 4096
- **THEN** Bridge uses that bounded capacity while the default remains 64, without changing edge disk capacity or overflow policy
