# Operations runbook

| Symptom | Likely cause | Action |
|---|---|---|
| Host exits at start with ATM-1013/1014/1015 | Invalid config, missing manifest, unreadable secret | Run `atmcfg verify <dir> --env <env>`; fix; rewrite manifest |
| Banner "Health: Disk is Unhealthy" | Low disk | Purge old logs, run database purge, check retention |
| Banner for a device fault | Device reported Fault in health check | Maintenance, device self-test; escalate to field engineer |
| No heartbeat on the API | Network or API down | Terminal keeps working; heartbeats queue in the outbox and drain on recovery. Check the circuit breaker log lines |
| ATM-3001 to 3004 | Database errors | Check disk, run backup/restore procedure below; audit falls back to a file store meanwhile and reconciles |
| Audit verification fails | Tampering or file corruption | Do not purge. Copy logs and the database off the terminal, run `VerifyAsync` via the Maintenance tool, report as a security incident |
| UI frozen | Dispatcher blocked | Watchdog logs the missed heartbeats; restart the Host; collect the last log file |

## Logs
Rolling JSON lines in `logs\`. Search: `atmlog search <dir> --level Error --since 1h` (see `atmlog --help`). Every line carries terminal id and correlation id; PAN and PIN are masked.

## Backup and restore
Database backup and restore are in `DatabaseMaintenance` (WAL checkpoint, copy, integrity check). Take a backup before every upgrade. Restore only with the Host stopped.

## Key rotation
`atmcfg rotate-key` adds a new key version; old values stay readable until re-encrypted with `atmcfg encrypt`. Audit key stays derived from v1, so keep v1 until the audit trail is archived.

## Reload without restart
Edit the Logging level or the idle timeout in config; the change is validated and applied, or ignored (last known good) if invalid.
