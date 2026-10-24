# Tasks

## 1. Value model

- [x] 1.1 Create `tests/Telemetrix.PlantSim.Tests` and add it to `Telemetrix.slnx`; verify `dotnet test` discovers it
- [x] 1.2 Implement `Model/PumpingLine` with the dynamics in design.md; verify seeded unit tests: 600 steps stay in normal ranges, flow is 0 within 2 steps of a stop, level rises while running and falls while stopped

## 2. OPC UA server

- [x] 2.1 Add OPCFoundation.NetStandard.Opc.Ua.Server to `Directory.Packages.props`; host `ApplicationInstance` in the worker on port 4840; verify UaExpert connects with SecurityMode None
- [ ] 2.2 Implement `PlantNodeManager` building `Plant01/Line01/Pump01|Tank01` and the 7 variables with NodeIds, types and engineering units; verify by browsing in UaExpert and an integration test that reads each NodeId and asserts namespace index 2
- [ ] 2.3 Drive the nodes from the value model at 1 Hz with UTC source timestamps; verify UaExpert shows one update per second per tag

## 3. Fault injection

- [ ] 3.1 Add the `Simulation` object with `TripPump`, `StartPump`, `SetBadQuality`, `Spike`, `RestartServer` methods; verify an integration test calls each method through the client SDK and observes the specified effect
- [ ] 3.2 Add `RandomFaultScheduler` with configurable rates and seed; verify a unit test with a fake clock that the expected faults occur over a simulated 6 hours and none occur when disabled
- [ ] 3.3 Document the methods and `Faults:*` settings in `src/Telemetrix.PlantSim/README.md`; verify each documented method name matches the address space

## 4. Deployment

- [ ] 4.1 Add a Dockerfile and the `plantsim` service (port 4840, pki volume) to `deploy/docker-compose.yml`; verify `docker compose up plantsim` and connect from UaExpert to `opc.tcp://localhost:4840`
- [ ] 4.2 Add a bridge configuration profile for PlantSim (endpoint + 7 NodeIds); verify it loads without code change

## 5. Milestone verification

- [ ] 5.1 Done-when check: switch the bridge from OPC PLC to PlantSim by configuration only; verify all 7 tags appear in `measurement` and `git diff` shows no change under `src/Telemetrix.Edge.Bridge/**/*.cs` or `src/Telemetrix.Transformer/**/*.cs`
- [ ] 5.2 Trigger `RestartServer` while the bridge runs; verify the bridge reconnects (NFR-04) and a gap event appears on `plant.events`
- [ ] 5.3 Update ROADMAP.md M6 status; verify CI is green
