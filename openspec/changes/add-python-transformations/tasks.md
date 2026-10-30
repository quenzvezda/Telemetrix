# Tasks

On 2026-10-31 the reviewer asked for issue #7 to be implemented to completion, accepting the agent's recommendation for each open decision (ADR-0016–0018) and reviewing the result as a PR. Final manual acceptance and archival remain the reviewer's.

## 1. Decisions and contract

- [x] 1.1 Record ADR-0016 (managed child process), ADR-0017 (label in Batch schema v2) and ADR-0018 (raw preservation, derived Tags, durable run record), the M10 roadmap row, CONTEXT.md terms and FR-26–FR-28 in the design document; verify `npx -y @fission-ai/openspec@1.14.3 validate add-python-transformations --strict` passes
- [x] 1.2 Add `TelemetryBatch.Transformation`, schema version 2 and the shared label rule to `Telemetrix.Contracts`; accept v1/v2 and dead-letter invalid labels in `DeserializeValidated`; verify `dotnet test --filter "FullyQualifiedName~TelemetryJson|FullyQualifiedName~InvalidBatch"` covers v1 bytes unchanged, v2 round-trip, v1 with a label, an invalid label and v3

## 2. Bridge label assignment

- [x] 2.1 Add `Transformations:Assets` options with startup validation and assign labels in `AssetBatcher`; verify `dotnet test tests/Telemetrix.Edge.Bridge.Tests --filter "FullyQualifiedName~AssetBatcher|FullyQualifiedName~TransformationAssignment"` shows labeled Assets as v2, unlabeled as v1 and `../evil.py` rejected

## 3. Registry and Python runtime

- [x] 3.1 Add `Transformations` options and the registry loader (labels, contained relative paths, SHA-256 versions and pins, declared outputs); verify `dotnet test tests/Telemetrix.Transformer.Tests --filter FullyQualifiedName~TransformationRegistry` covers a valid registry and each startup failure
- [x] 3.2 Add the Python runner and `PythonWorker` (handshake, load, transform, stdout isolation, timeout kill, crash restart, start failure as transient); verify `dotnet test tests/Telemetrix.Transformer.Tests --filter FullyQualifiedName~PythonWorker` covers success, exception, timeout, `os._exit`, print noise, NaN and load failure

## 4. Validation, dispositions and storage

- [x] 4.1 Add the output validator and transformation engine (dispositions, derived Quality and range checks, failure records, derived Tag resolution through an interface); verify `dotnet test tests/Telemetrix.Transformer.Tests --filter "FullyQualifiedName~TransformationOutput|FullyQualifiedName~TransformationEngine"` covers every disposition, empty output and each malformed case
- [ ] 4.2 Add migration `003_transformations.sql`, the Postgres derived Tag catalog, run lookup and the atomic run/derived/disposition write in `MeasurementWriter`; wire the engine into `TelemetryBatchHandler` and DI with fail-fast startup; verify `dotnet test tests/Telemetrix.Integration.Tests --filter FullyQualifiedName~TransformationStorage` shows the migration applies twice, the transaction is atomic, a conflicting output Tag fails and a recorded Batch is not transformed again

## 5. Observability, examples and deployment

- [ ] 5.1 Add transformation metrics and correlated run logs; verify `dotnet test --filter "FullyQualifiedName~TransformationMetrics"` observes runs, duration, samples in/out and skips with label, version and outcome
- [ ] 5.2 Add the four examples and registry under `deploy/transformations/` with a README (contract, installation, folder mount, dependencies, trust boundary); verify `dotnet test tests/Telemetrix.Transformer.Tests --filter FullyQualifiedName~TransformationExamples` runs each example through the real runner with the documented results
- [ ] 5.3 Install `python3` in the Transformer image and add compose variables and the read-only scripts mount, disabled by default; verify `docker compose -f deploy/docker-compose.yml --profile pipeline config` renders the mount and `docker build --target transformer -f deploy/DeliveryReview.Dockerfile .` runs `python3 --version`

## 6. Milestone verification

- [ ] 6.1 Run Kafka + TimescaleDB end to end: two Assets select two scripts, an unlabeled Batch keeps the built-in result, a disabled host stores labeled Batches without migration 003, and unknown label / exception / timeout / malformed / empty outcomes are recorded with raw Measurements intact; verify `dotnet test tests/Telemetrix.Integration.Tests --filter FullyQualifiedName~TransformationPipeline`
- [ ] 6.2 Prove replay and crash guarantees: a Batch replayed after a script-version change keeps its counts, derived values and run version; a transformer killed after the transaction but before the commit stores no duplicate; per-Asset order and source timestamps are preserved; verify `dotnet test tests/Telemetrix.Integration.Tests --filter FullyQualifiedName~TransformationReplay`
- [ ] 6.3 Measure per-Batch runtime cost at 500 Samples and record it against NFR-06/NFR-07 in `docs/m10-review.md`; verify `dotnet test tests/Telemetrix.Transformer.Tests --filter FullyQualifiedName~TransformationPerformance` and the full Release suite pass with zero warnings
- [ ] 6.4 Hand the reviewer the M10 review package (manual Docker/Kafka UI/SQL/Grafana steps for issue #7's acceptance criteria) and open the PR; verify CI is green on the PR
