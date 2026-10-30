---
status: accepted
---

# Run Python transformations in a managed child process

Issue #7 asks for operator-supplied Python transformations without rebuilding the C# Transformer. The reviewer delegated the open design choices to the agent's recommendation on 2026-10-31; this ADR records the runtime boundary.

The Transformer starts one long-lived Python worker (`python -I -u telemetrix_runner.py`) and talks to it over stdin/stdout with newline-delimited JSON, one request per Batch. The runner ships with the Transformer; the scripts and their registry come from a configured local directory. At startup the Transformer reads every registered script, computes its SHA-256 version, sends the source text to the worker and fails fast when a script is missing, does not compile or lacks its entry point. Scripts therefore stay pinned to the versions loaded at startup until the next deployment, even if the files change on disk.

Each request has a timeout. A timeout or a worker that dies mid-request kills the process tree, records a permanent failure for that Batch and the next request restarts the worker. Failing to start the worker during operation is transient: the offset is retained and retried with backoff, like a database outage (FR-24). On POSIX the runner applies an optional address-space limit; output size is bounded by the Transformer.

Operator scripts are trusted code, at the same level as Transformer configuration. The child process runs with the Transformer's privileges; isolation covers crashes, hangs and stdout corruption, not malicious code. A Batch can only select a registered label, never a path or executable. Scripts must be deterministic and free of external side effects, because a Batch can be transformed again after a crash.

## Considered Options

- **Embedded interpreter (pythonnet / CPython in-process)** — lowest per-call cost, but a script crash, hang or native-extension fault takes down the consumer thread and Kafka session, and the GIL and native packaging complicate the Linux image.
- **Process per Batch** — simplest isolation, but Python start-up (~30–60 ms) at ten Batches per second costs most of a core and adds latency to NFR-06.
- **Separate worker service over Kafka or HTTP** — independent scaling, but adds a second output whose success must be coordinated with offset commits, which ADR-0012 deliberately deferred.

## Consequences

Python 3.10+ becomes a runtime dependency of the Transformer only when transformations are enabled; the container image installs `python3`. Third-party packages go into the interpreter's environment (for example a virtual environment selected by `Transformations:PythonExecutable`) and are outside the script version hash, so operators pin them in their image. Changing a script requires restarting the Transformer.
