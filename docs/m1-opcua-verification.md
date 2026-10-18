# M1 OPC UA verification

Verified manually by the reviewer in UaExpert on 2026-10-18 for
`add-local-environment`, task 2.1.

## Connection and observations

- Endpoint entered: `opc.tcp://localhost:50000`, using the local development
  connection flow with security None and Anonymous authentication.
- Discovery returned the container hostname. UaExpert's **Replace Hostname**
  prompt was accepted to use `localhost` from Windows.
- UaExpert initially reported `BadCertificateUntrusted` for the OPC PLC server;
  the reviewer completed the certificate acceptance step and connected.
- `Objects/OpcPlc/Telemetry` was browsable. The reviewer confirmed that values
  changed in Data Access View; screenshots also showed data types, source
  timestamps, server timestamps and status codes.
- The final screenshot showed an Uncertain status for `BadFastUInt1` and Good
  for the other displayed nodes. The capture therefore does not imply that
  every sample always has Good quality.

## Observed node inventory

UaExpert displays string NodeIds as `NS3|String|<identifier>`. The equivalent
textual NodeIds below use the namespace index observed in this running server.
Recheck the namespace table when configuring the M2 client.

| Identifier | NodeId | Data type |
| --- | --- | --- |
| AlternatingBoolean | `ns=3;s=AlternatingBoolean` | Boolean |
| RandomSignedInt32 | `ns=3;s=RandomSignedInt32` | Int32 |
| RandomUnsignedInt32 | `ns=3;s=RandomUnsignedInt32` | UInt32 |
| StepUp | `ns=3;s=StepUp` | UInt32 |
| BadFastUInt1 | `ns=3;s=BadFastUInt1` | UInt32 |
| FastUInt1 | `ns=3;s=FastUInt1` | UInt32 |
| BadSlowUInt1 | `ns=3;s=BadSlowUInt1` | UInt32 |
| SlowUInt1 | `ns=3;s=SlowUInt1` | UInt32 |
| DipData | `ns=3;s=DipData` | Double |
| NegativeTrendData | `ns=3;s=NegativeTrendData` | Double |
| PositiveTrendData | `ns=3;s=PositiveTrendData` | Double |
| SpikeData | `ns=3;s=SpikeData` | Double |

These are simulator nodes available as candidates for the seven tag mappings
in M2. The physical tag mapping and any simulator range configuration still
belong to that milestone.
