"""Telemetrix transformation runner, contract version 1 (ADR-0016).

The Transformer starts this file as `python -I -u telemetrix_runner.py` and exchanges one JSON
object per line: a ready line first, then one response per `load` or `transform` request.
Operator scripts are trusted configuration; this process isolates crashes, hangs and stray output.
"""

import argparse
import io
import json
import os
import platform
import sys
import traceback

CONTRACT_VERSION = 1
MAX_MESSAGE = 1024


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--memory-limit-mb", type=int, default=0)
    parser.add_argument("--max-output-samples", type=int, default=10000)
    args = parser.parse_args()

    # Keep a private handle on the protocol pipe, then send fd 1 (print, os.write(1, ...)) to stderr.
    protocol = io.open(os.dup(1), "w", encoding="utf-8", newline="\n")
    os.dup2(2, 1)
    sys.stderr.reconfigure(encoding="utf-8", errors="backslashreplace")
    sys.stdout = sys.stderr
    requests = io.TextIOWrapper(sys.stdin.buffer, encoding="utf-8")

    apply_memory_limit(args.memory_limit_mb)
    send(protocol, {"ok": True, "ready": True, "contractVersion": CONTRACT_VERSION,
                    "python": platform.python_version()})

    entries = {}
    for line in requests:
        if not line.strip():
            continue
        request = json.loads(line)
        operation = request.get("op")
        if operation == "load":
            send(protocol, load(entries, request))
        elif operation == "transform":
            send(protocol, transform(entries, request, args.max_output_samples))
        else:
            send(protocol, failure("protocol-error", f"unknown op {operation!r}"))


def apply_memory_limit(megabytes):
    if megabytes <= 0:
        return
    try:
        import resource
        limit = megabytes * 1024 * 1024
        resource.setrlimit(resource.RLIMIT_AS, (limit, limit))
    except (ImportError, ValueError, OSError) as error:
        print(f"memory limit not applied: {error}", file=sys.stderr)


def load(entries, request):
    label = request["label"]
    namespace = {"__name__": "telemetrix_transformation_" + label.replace("-", "_"),
                 "__file__": request["filename"]}
    try:
        code = compile(request["source"], request["filename"], "exec")
        exec(code, namespace)
    except BaseException:
        return failure("load-failed", traceback.format_exc(limit=-3))
    entry = namespace.get(request["entryPoint"])
    if not callable(entry):
        return failure("load-failed", f"entry point {request['entryPoint']!r} is not a callable")
    entries[label] = entry
    return {"ok": True}


def transform(entries, request, max_output_samples):
    entry = entries.get(request["label"])
    if entry is None:
        return failure("protocol-error", f"transformation {request['label']!r} is not loaded")
    try:
        result = entry(request["batch"], request["context"])
    except BaseException:
        # SystemExit and MemoryError raised by a script are script errors, not runner exits.
        return failure("script-error", traceback.format_exc(limit=-3))
    if not isinstance(result, dict):
        return failure("malformed-output", f"transform must return a dict, not {type(result).__name__}")
    samples = result.get("samples")
    if isinstance(samples, list) and len(samples) > max_output_samples:
        return failure("malformed-output", f"{len(samples)} output samples exceed the limit of {max_output_samples}")
    return {"ok": True, "result": result}


def failure(reason, message):
    return {"ok": False, "reason": reason, "message": message[-MAX_MESSAGE:]}


def send(protocol, response):
    try:
        line = json.dumps(response, allow_nan=False, separators=(",", ":"))
    except (TypeError, ValueError, RecursionError) as error:
        # NaN, infinity and non-JSON objects in a result are malformed output.
        line = json.dumps(failure("malformed-output", f"result is not valid JSON: {error}"))
    protocol.write(line + "\n")
    protocol.flush()


if __name__ == "__main__":
    main()
