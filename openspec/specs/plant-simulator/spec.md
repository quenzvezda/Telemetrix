# plant-simulator Specification

## Purpose

Provides a reproducible pumping-line OPC UA source with correlated telemetry and controllable faults for pipeline development and recovery verification.

## Requirements

### Requirement: Pumping line address space

The simulator SHALL expose an OPC UA server whose address space contains the seven tags of the data model under `Objects/Plant01/Line01/<Asset>/`, in namespace index 2, with string NodeIds of the form `Line01.<Asset>.<Tag>`, the data model's data types, and engineering units (FR-01).

#### Scenario: Browse the line
- **WHEN** a client browses `Objects/Plant01/Line01`
- **THEN** it SHALL find the assets Pump01 and Tank01 with these variables:
  - `ns=2;s=Line01.Pump01.Running` (Boolean)
  - `ns=2;s=Line01.Pump01.FlowRate` (Double, m³/h)
  - `ns=2;s=Line01.Pump01.DischargePressure` (Double, bar)
  - `ns=2;s=Line01.Pump01.MotorTemperature` (Double, °C)
  - `ns=2;s=Line01.Pump01.Vibration` (Double, mm/s)
  - `ns=2;s=Line01.Tank01.Level` (Double, %)
  - `ns=2;s=Line01.Tank01.Temperature` (Double, °C)

#### Scenario: Read a tag by NodeId
- **WHEN** a client reads `ns=2;s=Line01.Pump01.FlowRate`
- **THEN** it SHALL receive a Double value with a Good status code and a UTC source timestamp

### Requirement: 1 Hz updates

The simulator SHALL update every tag once per second, each update carrying a new UTC source timestamp (FR-01).

#### Scenario: Update rate
- **WHEN** the simulator runs for 10 minutes
- **THEN** each tag SHALL have received about 600 updates with strictly increasing source timestamps

### Requirement: Normal ranges

Without faults, the simulator SHALL keep values within the data model's normal ranges: FlowRate 40–60 m³/h while running, DischargePressure 3.5–5.0 bar while running, MotorTemperature 45–80 °C, Vibration 0.5–4.5 mm/s, Level 20–90 %, Tank Temperature 15–30 °C (FR-01).

#### Scenario: Normal operation
- **WHEN** the simulator runs for 10 minutes without faults and the pump running
- **THEN** every value SHALL be within its normal range

### Requirement: Correlated dynamics

The simulator SHALL correlate tags like the physical line: FlowRate is 0 while Running is false; DischargePressure follows FlowRate with noise; MotorTemperature rises slowly while running and falls while stopped; Tank Level rises while the pump runs and falls while it is stopped (FR-01).

#### Scenario: Pump stopped
- **WHEN** Running becomes false
- **THEN** FlowRate SHALL be 0 within 2 seconds
- **AND** DischargePressure, MotorTemperature and Tank Level SHALL start decreasing

#### Scenario: Pump running
- **WHEN** the pump has been running for several minutes after a stop
- **THEN** MotorTemperature and Tank Level SHALL be increasing

### Requirement: Fault injection on demand

The simulator SHALL let an operator trigger each fault on demand without restarting it: pump trip, Bad-quality status on a chosen tag for a duration, a single value spike on a chosen tag, and a server restart (FR-02).

#### Scenario: Pump trip
- **WHEN** a pump trip is triggered
- **THEN** Running SHALL become false and FlowRate SHALL drop to 0

#### Scenario: Bad quality
- **WHEN** Bad quality is triggered on Tank01.Temperature for 30 seconds
- **THEN** that tag SHALL report a Bad status code for 30 seconds and then Good again

#### Scenario: Value spike
- **WHEN** a spike is triggered on Pump01.Vibration
- **THEN** one update SHALL carry a value above the tag's normal range, followed by values back in range

#### Scenario: Server restart
- **WHEN** a server restart is triggered
- **THEN** all client sessions SHALL be dropped and the server SHALL accept connections again within 10 seconds

### Requirement: Random faults

The simulator SHALL optionally inject faults at random at configurable rates; by default the pump trips about once an hour and restarts after a configurable delay, Pump01.Vibration spikes occasionally, and Tank01.Temperature occasionally reports Bad quality (FR-02).

#### Scenario: Random mode disabled
- **WHEN** random faults are disabled in configuration
- **THEN** no fault SHALL occur unless triggered on demand

#### Scenario: Default random mode
- **WHEN** the simulator runs for several hours with default settings
- **THEN** pump trips, vibration spikes and Bad-quality periods SHALL each have occurred at least once

### Requirement: Drop-in replacement for OPC PLC

The bridge SHALL be able to switch from OPC PLC to the simulator by changing configuration only (endpoint and tag list), with no code change in any service.

#### Scenario: Switch source
- **WHEN** the bridge configuration points at the simulator endpoint and its NodeIds
- **THEN** the seven tags SHALL be stored in `measurement` with no change to bridge or transformer code
