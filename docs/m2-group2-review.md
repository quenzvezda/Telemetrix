# M2 group 2 review: OPC UA subscription

Change: `add-opcua-subscription`, tasks 2.1–2.4.
Branch: `m2-add-opcua-subscription`.
Implementation is in progress; reviewer acceptance is pending.

## Verification evidence

- Task 2.1: 24 Bridge tests passed, including Good, Uncertain, Bad, boolean true/false and missing-value notification mapping. Source/server timestamps and the raw status code are preserved.
- Task 2.2: on 2026-10-18, a temporary verification harness ran the adapter against the existing OPC PLC container at `opc.tcp://localhost:50000`. It appended `Pump01/Bogus` at `ns=3;s=DoesNotExist` to the seven development tags. The adapter logged `BadNodeIdUnknown` (`0x80340000`) for that tag while delivering 37 samples from all seven healthy tags; the harness then cancelled the adapter and exited successfully. The server accepted sampling 500 ms, publishing 1,000 ms and queue size 10. `Tank01/Temperature` retained Uncertain status `0x40900000`.

The harness lives outside the repository; repeatable reviewer commands will be
included here when the ingestion worker is implemented in task 2.3.
