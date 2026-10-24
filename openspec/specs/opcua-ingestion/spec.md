# opcua-ingestion Specification

## Purpose

Collect samples from OPC UA servers through configurable tag subscriptions or browse discovery, preserving source timestamps and raw status codes and resuming collection after a server restart.

## Requirements

### Requirement: Configurable OPC UA connection and tag list
The bridge SHALL read the OPC UA endpoint URL, security mode and the list of tags (asset, tag name, NodeId) from `appsettings.json`, and every value SHALL be overridable by an environment variable (FR-10).

#### Scenario: Endpoint overridden by environment
- **WHEN** `appsettings.json` names `opc.tcp://localhost:50000` and the environment sets the endpoint to `opc.tcp://opc-plc:50000`
- **THEN** the bridge connects to `opc.tcp://opc-plc:50000`

#### Scenario: Invalid configuration
- **WHEN** a configured tag has no NodeId, or the endpoint is not an `opc.tcp://` URL
- **THEN** the bridge fails at startup with a message naming the offending setting

### Requirement: One subscription with a monitored item per tag
The bridge SHALL create one subscription with one monitored item per configured tag, using the configured sampling interval (default 500 ms), publishing interval (default 1,000 ms) and queue size (FR-11).

#### Scenario: Default intervals
- **WHEN** the bridge starts with 7 configured tags and no interval overrides
- **THEN** the server shows one subscription with a 1,000 ms publishing interval and 7 monitored items sampling at 500 ms

#### Scenario: Node does not exist
- **WHEN** one configured NodeId is unknown to the server
- **THEN** the bridge logs that tag as failed with the server's status code and still monitors the other tags

### Requirement: Tag discovery by browsing
When a browse start node is configured instead of a tag list, the bridge SHALL browse beneath it and monitor every variable found, deriving asset and tag names from the browse path (FR-12).

#### Scenario: Browse the pumping line
- **WHEN** the browse start node is `Objects/Plant01/Line01`
- **THEN** the bridge monitors one item per variable under each asset object, with the asset taken from the parent object and the tag from the variable's browse name

#### Scenario: Browse OPC PLC telemetry
- **WHEN** the browse start node is `Objects/OpcPlc/Telemetry`
- **THEN** the bridge monitors every variable found there using the server's browse names, and the discovered NodeIds include all seven development mappings even though the static aliases differ
- **AND** the browse start node takes precedence over any configured static tag list

### Requirement: Sample shape
Every data-change notification SHALL become exactly one sample carrying plant, line, asset, tag, value, source timestamp, server timestamp and the raw OPC UA status code (FR-11). Timestamps MUST be UTC, and the source timestamp MUST be the one the server reports, never the bridge's clock (ADR-0009).

Sample values SHALL use nullable double precision numbers. Source adapters SHALL convert booleans to 1/0 before emitting samples; string values are outside the v1 scope (ADR-0010).

#### Scenario: Good value
- **WHEN** the server reports `MotorTemperature = 71.3` with status Good and source timestamp 09:15:01.000 UTC
- **THEN** the sample has value 71.3, status code 0 and source timestamp 09:15:01.000 UTC

#### Scenario: Bad value is kept
- **WHEN** the server reports a value with a Bad status code
- **THEN** a sample is still produced with that status code; judging quality is the transformer's job

#### Scenario: Boolean value
- **WHEN** the server reports `Running = true`
- **THEN** the sample value is 1

#### Scenario: Boolean false value
- **WHEN** the server reports `Running = false`
- **THEN** the sample value is 0

### Requirement: Resume after server restart
When the OPC UA session is lost, the bridge SHALL keep retrying and, once the server is reachable again, SHALL resume delivering samples for every configured tag without a process restart (FR-11).

#### Scenario: Simulator container restarted
- **WHEN** the OPC UA server is restarted while the bridge runs
- **THEN** samples stop during the outage and resume for all tags once the server is back, with no manual action

### Requirement: Automatic reconnection with backoff

The bridge SHALL detect a lost OPC UA session and reconnect with exponential backoff (bounded initial and maximum delay, with jitter) until the server is available again, without a process restart (FR-16).

#### Scenario: Server unavailable for a while
- **WHEN** the OPC UA server is unreachable for several minutes
- **THEN** the bridge SHALL keep retrying with increasing delays up to the configured maximum
- **AND** SHALL NOT exit

### Requirement: Recovery within 30 seconds

Once the OPC UA server is reachable again, the bridge SHALL have restored its session and all monitored items within 30 seconds and resume delivering samples (NFR-04).

#### Scenario: Simulator container restarted
- **WHEN** the simulator container is restarted
- **THEN** samples for every configured tag SHALL arrive again within 30 seconds of the server accepting connections
- **AND** a gap SHALL be reported on `plant.events`
