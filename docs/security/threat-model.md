# STRIDE threat model (Phase 1 platform)

Scope: the terminal software built in Phase 1 (Host, logging, audit, configuration, persistence, event bus, monitoring client) and the Monitoring API. Card, PIN and cash devices are simulators in Phase 1, so physical and device-channel threats are listed as open and belong to Phase 2.

Trust boundaries: (1) terminal OS user vs. the ATM process, (2) ATM process vs. its files (config, keys, database, logs), (3) terminal vs. Monitoring API over the network, (4) supervisor vs. customer on the same screen.

| # | STRIDE | Threat | Mitigation in code | Status |
|---|---|---|---|---|
| T1 | Spoofing | Rogue terminal posts to the Monitoring API | `X-Api-Key` required, fails closed when unset outside Development (`Program.cs`); HTTPS expected in front | Mitigated; per-terminal keys and mTLS are Phase 2 |
| T2 | Spoofing | Customer gets into the maintenance screen | PBKDF2 supervisor session with lockout and timeout (`Supervisor.cs`) | Mitigated for Phase 1 credentials; badge/PKI login is Phase 2 |
| T3 | Tampering | Audit records edited or deleted | HMAC-SHA256 hash chain, key derived from protected key v1, purge writes an anchor entry, `VerifyAsync` detects any break | Mitigated; the key holder can still forge, so ship the chain off-box (Phase 2) |
| T4 | Tampering | Config edited on disk to weaken security | Prod requires `config.manifest` (SHA-256 of every file), refuses debug features and plaintext secrets, invalid config blocks startup | Mitigated for offline edits; an attacker with admin rights can regenerate the manifest |
| T5 | Tampering | Concurrent writes corrupt a record | Optimistic concurrency token on every entity, WAL, `synchronous=FULL` | Mitigated |
| T6 | Repudiation | Supervisor denies an action | Every supervisor action goes through `IAuditService` with terminal id, correlation id, UTC time | Mitigated |
| T7 | Information disclosure | PAN or PIN reaches a log, audit entry or error report | PAN only ever held masked (ADR-007); `MaskingSink` always masks and cannot be disabled; audit inputs masked before hashing; tests feed full PANs and check output | Mitigated |
| T8 | Information disclosure | Secrets in config or repo | `enc:v{n}:` AES-GCM values, DPAPI key on Windows, gitleaks job in CI, `atmcfg verify` flags plaintext | Mitigated; on Linux the file key provider is Dev/Sit only and Prod refuses to start |
| T9 | Information disclosure | Stack traces shown to customers | Customer UI shows catalogue codes (ATM-xxxx) only; detail goes to logs | Mitigated |
| T10 | Denial of service | Disk fills with logs | Rolling files with retention, disk-space health check raises a banner and notification | Mitigated |
| T11 | Denial of service | Slow dependency hangs the terminal | Polly timeout, retry, circuit breaker around monitoring calls; outbox keeps heartbeats when API is down | Mitigated |
| T12 | Denial of service | Flood of events stalls UI | Bounded channels event bus, subscriber faults isolated to a dead-letter store | Mitigated |
| T13 | Denial of service | Process hangs unnoticed | Dispatcher heartbeat every 5 s, watchdog, 10 s shutdown timeout | Mitigated; an external watchdog service is Phase 2 |
| T14 | Elevation of privilege | Debug features left on in production | Prod validation rejects `DebugFeatures=true` | Mitigated |
| T15 | Elevation of privilege | Vulnerable dependency | `dotnet list package --vulnerable` gate and CodeQL in CI, transitive pins in `Directory.Packages.props` (4 advisories fixed in Sprint 6) | Mitigated; advisories published later need the scheduled scan |
| T16 | All | Device-channel attacks (skimmer, black box, PIN pad tamper) | Devices only behind `IDevicePort` | Open, Phase 2 |
| T17 | Information disclosure | SQLite database copied from the terminal | Only masked PANs stored; database is not encrypted at rest | Open: add disk encryption (BitLocker) per deployment guide, SQLCipher in Phase 2 |

Residual risks to take to the bank's security review: T3 (off-box audit shipping), T4 (admin can re-sign config), T16 and T17.
