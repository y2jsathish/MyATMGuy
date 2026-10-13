# PCI DSS v4.0 mapping (indicative)

This maps platform controls to PCI DSS requirements so the assessor starts from the right place. It is not a certification. Cardholder-data handling (PIN block, encryption of track data, P2PE) arrives with the device adapters in Phase 2.

| Requirement | What the platform provides | Evidence |
|---|---|---|
| 3.3 Mask PAN when displayed (first 6, last 4 at most) | `MaskedPan` value object; no unmasked PAN type exists | ADR-007, Core tests, masking tests |
| 3.4 / 3.5 Render stored PAN unreadable | Only masked PAN is stored; sensitive config values AES-GCM encrypted; key in DPAPI | `SecretProtector` tests, `atmcfg verify` |
| 3.6 / 3.7 Key management | Versioned keys (`enc:v{n}`), `atmcfg rotate-key`, audit key derived from key v1 | Sprint 3 tests |
| 6.2 Secure development | Warnings as errors, analyzers, CodeQL, architecture tests | `ci.yml`, `codeql.yml` |
| 6.3.3 Patch vulnerable components | Vulnerability gate in CI, transitive pins | `ci.yml`, `Directory.Packages.props` |
| 6.5 Change control | Config manifest, migrations versioned in source, release checklist | `docs/ops/release-checklist.md` |
| 7 / 8 Restrict access, authenticate | Supervisor session (PBKDF2, lockout, idle timeout); real PKI/badge login is Phase 2 | Supervisor tests |
| 10.2 Audit logs of actions | Hash-chained, HMAC-signed audit trail; DB store with file fallback | `AuditService` tests, integration tests |
| 10.3 Protect audit logs | Chain verification, purge anchor; no update or delete API | `VerifyAsync` tests |
| 10.4 / 10.5 Review and retain | `atmlog` search tool, rolling retention | `tools/ATM.LogViewer` |
| 10.6 Time sync | All times UTC via `TimeProvider`; OS time sync is a deployment task | Deployment guide |
| 11.3 Vulnerability scans | `dotnet list package --vulnerable`, CodeQL (`codeql.yml`) | CI |
| 12.10 Incident response | Error catalogue, error counters, notifications, runbook | `docs/ops/runbook.md` |

Gaps owned by the deployment or Phase 2: disk encryption, OS hardening, network segmentation, PIN translation security, P2PE, penetration test.
