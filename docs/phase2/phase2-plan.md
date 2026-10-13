# Phase 2 plan (draft)

The Phase 1 blueprint only names Phase 2 themes: XFS and XFS4IoT device integration, the transaction engine, host connectivity, monitoring, software distribution. This draft turns them into slices, ordered so that everything that can be proven without hardware is built first, and so that each hardware-dependent slice has a clear test on the lab machine (a Diebold Nixdorf DN 200V is available for field tests).

| Slice | Content | Needs hardware or the bank? | Status |
|---|---|---|---|
| P2-1 | Cash withdrawal flow over the device ports and a host port: limits, pending record before the host call, reversal on host timeout or dispense failure, receipt after cash, card always ejected | No (simulated devices and host) | Done, tested |
| P2-2 | Balance inquiry, mini statement, PIN change flows on the same pattern; customer UI screens driven by the flow outcome | No | Next |
| P2-3 | Reversal retry job: reversals that could not be delivered are queued durably and retried until the host confirms; manual reconciliation report | No | Planned |
| P2-4 | Host adapter for the bank's real protocol (ISO 8583 is the common case) behind `IHostGateway`, with the simulator as the contract test | Needs the host spec and a test host | Blocked on the bank |
| P2-5 | XFS 3.x adapter behind the device ports: start with read-only status of each service (card reader, PIN pad, dispenser, printer), then command execution | Needs the DN 200V and its XFS installation | Blocked on field report |
| P2-6 | XFS4IoT adapter (WebSocket) for terminals that expose it | Needs a terminal with XFS4IoT | Later |
| P2-7 | mTLS to the monitoring API, per-terminal keys, signed software updates, kiosk lockdown baseline | Partly | Later |

## How the DN 200V helps now
Run `atmfieldtest.exe` (see `docs/ops/field-test.md`) and send back the report. Its XFS section lists the logical services and providers installed on the machine, which decides what P2-5 has to target. Nothing in P2-5 will be coded blind: the first adapter build will only read device status, so a first run on the machine cannot move cash.

## Decisions taken in P2-1
- The transaction is stored Pending before the host is asked, so a crash never loses a transaction.
- A host that does not answer is treated as possibly approved: a reversal is sent. If the reversal cannot be delivered it is audited as Critical and counted (ATM-5004); the durable retry is P2-3.
- A failed receipt never undoes a withdrawal.
- Customer messages carry no device or host internals.
- `IHostGateway` lives in Application; adapters are plug-ins like the device ports (ADR-004, ADR-017).
