# Tasks

## 1. Contract and dependencies

- [x] 1.1 Add `MQTTnet` to `Directory.Packages.props` and reference it from `Telemetrix.MqttSim` and `Telemetrix.Edge.Bridge`; verify `dotnet build` succeeds
- [x] 1.2 Add `MqttTagPayload` and the quality-string constants to `Telemetrix.Contracts`; verify a round-trip serialization test (numeric and boolean value) passes in `Telemetrix.Edge.Bridge.Tests`

## 2. MQTT simulator

- [x] 2.1 Share the pumping-line value model with PlantSim (extract, not copy) and verify both projects build against it
- [ ] 2.2 Implement MqttSim publishing all 7 tags at 1 Hz to `plant/Plant01/Line01/{asset}/{tag}` with QoS 1 and retain; verify with `docker compose exec mosquitto mosquitto_sub -t 'plant/#' -v` showing ~7 messages per second
- [ ] 2.3 Add connection retry on startup; verify by starting MqttSim before `mosquitto` and seeing it begin publishing once the broker starts
- [ ] 2.4 Document how to run MqttSim (command, configuration keys) in the project README section; verify the documented command works as written

## 3. Bridge MQTT source

- [ ] 3.1 Implement topic parsing and quality → status-code mapping; verify unit tests cover good/uncertain/bad/unknown quality and malformed topics
- [ ] 3.2 Implement `MqttSourceAdapter` (subscribe, persistent session, emit samples to the batching channel, `source` = `mqtt`); verify integration tests with a Mosquitto Testcontainer produce samples with the right identity, numeric/boolean value and UTC timestamps, preserve queued QoS 1 messages across normal reconnects, and honor buffer-capacity pause/resume with durable events and cleared pause history
- [ ] 3.3 Select the source via `Source:Kind` configuration; verify the bridge starts with `Source__Kind=Mqtt` and logs the MQTT endpoint
- [ ] 3.4 Reconnect with backoff and publish connection-lost / restored plant events; verify by restarting the `mosquitto` container and reading both events in `plant.events` via Kafka UI
- [ ] 3.5 Count and rate-limit-log malformed messages; verify a unit test feeding a bad payload increments the rejected counter and the adapter keeps running

## 4. Milestone verification

- [ ] 4.1 Run the stack with MqttSim and the bridge on `Source:Kind=Mqtt`; verify rows for all 7 tags appear in `measurement` with no transformer code or configuration change (M7 "Done when")
- [ ] 4.2 Compare the same 10 minutes from OPC UA and MQTT runs; verify both give the same row count per tag (± the samples sent during the switch) and note the protocol comparison in the change's notes
