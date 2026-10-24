## ADDED Requirements

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
