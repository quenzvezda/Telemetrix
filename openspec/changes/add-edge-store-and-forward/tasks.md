# Tasks

## 1. Decision

- [ ] 1.1 Resolve D3 (buffer limit and overflow policy) and record ADR; verify `docs/adr/` has the new ADR, ROADMAP.md marks D3 decided, and `specs/edge-buffering/spec.md` has a requirement + scenario for the limit

## 2. Store-and-forward buffer

- [ ] 2.1 Add Microsoft.Data.Sqlite to `Directory.Packages.props` and the bridge; create the `outbox` table on startup (WAL, `synchronous=FULL`); verify with a unit test that opens a temp file and finds the table
- [ ] 2.2 Implement append / read-oldest / delete in `Buffer/`; verify unit tests for FIFO order and restart survival (reopen the file, rows still there in order)
- [ ] 2.3 Implement the Direct/Draining publisher state machine in `Publishing/`; verify unit tests with a fake producer: failure switches to Draining, new batches queue behind buffered ones, empty buffer returns to Direct
- [ ] 2.4 Add `bridge.buffer.batches` / `bridge.buffer.samples` gauges and the start/empty log messages; verify with a unit test using `MeterListener`
- [ ] 2.5 Document buffer location and settings in the bridge `appsettings.json` comments / README section; verify the documented path matches the default

## 3. OPC UA reconnect

- [ ] 3.1 Add keep-alive monitoring and `SessionReconnectHandler`, with session recreate fallback on server restart; verify manually by restarting `opc-plc` and seeing samples resume
- [ ] 3.2 Add exponential backoff (1 s → 15 s, ±20 % jitter); verify a unit test of the delay sequence

## 4. Plant events

- [ ] 4.1 Add `PlantEvent` to `Telemetrix.Contracts`; verify a JSON round-trip unit test
- [ ] 4.2 Create `plant.events` (3 partitions, 30 d) at bridge startup; verify settings in Kafka UI
- [ ] 4.3 Emit ConnectionLost / ConnectionRestored / Gap through the store-and-forward queue; verify a unit test with a fake source that drops and restores, asserting event order and gap bounds

## 5. Milestone verification

- [ ] 5.1 Integration test (Testcontainers Kafka): pause the Kafka container, produce batches, restart the bridge host, unpause; verify all batches arrive once, in order
- [ ] 5.2 NFR-02: `docker compose stop kafka` for 30 minutes under default load, then start; verify measurement count equals samples sent
- [ ] 5.3 NFR-04: restart the simulator container; verify samples resume within 30 s and `plant.events` shows lost → restored → gap
- [ ] 5.4 Update ROADMAP.md M5 status; verify CI is green
