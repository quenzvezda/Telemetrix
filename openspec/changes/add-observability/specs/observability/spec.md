## ADDED Requirements

### Requirement: Health endpoint
Every service SHALL expose an HTTP `/health` endpoint that reports healthy only when its critical dependencies are reachable — the source and Kafka for the bridge, Kafka and the database for the transformer (FR-41).

#### Scenario: All dependencies up
- **WHEN** `/health` is requested while all dependencies of the service are reachable
- **THEN** it responds 200 with status Healthy

#### Scenario: Dependency down
- **WHEN** Kafka is stopped and the bridge's `/health` is requested
- **THEN** it responds with a non-200 status naming Kafka as the failing check, while the bridge keeps collecting into its buffer

### Requirement: Pipeline metrics
The services SHALL export OpenTelemetry metrics for samples received, batches produced, store-and-forward buffer depth, consumer lag and rows written, each labelled with the service name and, where applicable, the asset (FR-41).

#### Scenario: Metrics reflect flow
- **WHEN** the pipeline runs at 70 samples/s for one minute
- **THEN** the samples-received and rows-written counters each increase by about 4,200 and the buffer-depth gauge stays at 0

#### Scenario: Buffer depth rises during an outage
- **WHEN** Kafka is unreachable for one minute while the bridge collects
- **THEN** the buffer-depth gauge rises during the outage and returns to 0 after Kafka is back and the buffer drains

#### Scenario: Consumer lag is visible
- **WHEN** the transformer is stopped while the bridge keeps producing
- **THEN** the consumer-lag metric for group `transformer` grows until the transformer is restarted

### Requirement: Correlated structured logs
Every service SHALL write structured JSON logs to the console, and every log entry about a batch in the bridge or the transformer SHALL include its `batchId` as a correlation property (NFR-12).

#### Scenario: Trace one batch end to end
- **WHEN** the logs of the bridge and transformer are filtered by one `batchId`
- **THEN** they show the batch being produced by the bridge and then consumed and written by the transformer

### Requirement: Latency and throughput targets
The pipeline SHALL keep the 95th percentile of `ingested_at - time` under 10 seconds at default batch settings (NFR-06), and SHALL sustain 5,000 samples/s on a developer laptop without consumer lag growing over a 15-minute run (NFR-07).

#### Scenario: Latency at default settings
- **WHEN** the stack runs at the default simulation rate for 30 minutes
- **THEN** the p95 of `ingested_at - time` over that period is below 10 seconds

#### Scenario: Sustained throughput
- **WHEN** the simulator is scaled to 5,000 samples/s for 15 minutes
- **THEN** consumer lag for group `transformer` shows no upward trend and no batch is dead-lettered
