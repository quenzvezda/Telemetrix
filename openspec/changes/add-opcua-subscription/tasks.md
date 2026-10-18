# Tasks

## 1. Contract and configuration

- [x] 1.1 Add the `Sample` record to `Telemetrix.Contracts` (plant, line, asset, tag, value, source/server timestamp in UTC, status code); verify `dotnet build`
- [x] 1.2 Add `OPCFoundation.NetStandard.Opc.Ua.Client` to `Directory.Packages.props` and reference it from the bridge; verify `dotnet restore`
- [x] 1.3 Add `OpcUaOptions` (endpoint, security mode, sampling/publishing interval, queue size, tags, optional browse start node) with validate-on-start, and map the 7 tags to OPC PLC nodes in `appsettings.Development.json`; verify unit tests: missing NodeId and non-`opc.tcp` URL fail validation, an environment variable overrides the endpoint

Group 1 implementation and automated verification are complete. Reviewer accepted the code review on 2026-10-18 and authorized group 2; see [the review package](../../../docs/m2-group1-review.md).

## 2. Subscription

- [x] 2.1 Define `ISourceAdapter` and implement `OpcUaSourceAdapter` (session, one subscription, one monitored item per tag, notification → `Sample` into the channel); verify unit tests of the notification-to-sample mapping for Good, Bad and boolean values
- [x] 2.2 Log monitored-item creation failures per tag and keep the rest; verify by adding one bogus NodeId and running against OPC PLC
- [ ] 2.3 Replace the template `Worker` with `OpcUaIngestionWorker`, which logs each sample (asset, tag, value, source timestamp, status code); verify by running the bridge against OPC PLC
- [ ] 2.4 Implement browse discovery from a start node (FR-12); verify it finds the same tags as the static list on OPC PLC's telemetry folder

## 3. Recovery

- [ ] 3.1 Add session recovery with `SessionReconnectHandler`; verify with `docker compose -f deploy/docker-compose.yml restart opc-plc` while the bridge runs — samples resume for all tags without restarting the bridge

## 4. Milestone check

- [ ] 4.1 Done when: values print every second with source timestamps and status codes, and the client resumes after the OPC PLC container restarts
