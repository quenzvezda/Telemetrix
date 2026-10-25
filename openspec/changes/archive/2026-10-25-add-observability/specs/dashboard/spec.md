## Purpose

Defines the operator dashboard for live tag trends, pump running state and durable source-gap history, using retained measurements and one-minute rollups.

## ADDED Requirements

### Requirement: Live trends per tag
The dashboard SHALL show a time-series trend for every tag, selectable by asset, refreshing at least every 10 seconds, drawn from rollups for ranges longer than a few hours and from raw measurements for shorter ranges (FR-40).

#### Scenario: Live trend
- **WHEN** an operator opens the dashboard for `Pump01` over the last 15 minutes
- **THEN** each Pump01 tag shows a trend that advances as new data arrives

#### Scenario: Long range uses rollups
- **WHEN** the time range is set to the last 7 days
- **THEN** the trend is drawn from the 1-minute rollups

### Requirement: Pump status
The dashboard SHALL show each pump's current running state and when it last changed (FR-40).

#### Scenario: Pump trip is visible
- **WHEN** `Pump01.Running` changes from true to false
- **THEN** the dashboard shows Pump01 as stopped within one refresh interval, with the time of the change

### Requirement: Data gaps
The dashboard SHALL show periods with no data for an asset, based on the bridge's gap and connection plant events, and MUST NOT interpolate trends across them (FR-40).

#### Scenario: OPC UA server restart shows a gap
- **WHEN** the OPC UA server is down for 60 seconds
- **THEN** the trends show a break for that period and the gap is listed with its start and end time

### Requirement: Durable plant event history
When plant event storage is enabled after migration, Transformer SHALL store Bridge's connection and gap events from `plant.events`, deduplicate by `eventId`, and commit offsets only after storage succeeds (FR-16, FR-40). Bridge SHALL retain ownership of topic creation.

#### Scenario: Stored gap history
- **WHEN** Bridge publishes ConnectionLost, ConnectionRestored and Gap events for an asset
- **THEN** each event is stored with its identity, UTC occurrence time and optional gap bounds for dashboard queries

#### Scenario: Duplicate event delivery
- **WHEN** Kafka replays an already stored event
- **THEN** storage keeps one event for that `eventId` and the replay offset is committed after the idempotent insert succeeds
