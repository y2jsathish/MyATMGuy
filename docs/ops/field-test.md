# Field test tool (`atmfieldtest.exe`)

One self-contained Windows file, no .NET install needed. Copy it to the ATM PC (for example a DN 200V) and run it from a command prompt. Get it from the CI run of the latest commit on `main`: Actions, CI, artifact `atm-fieldtest-win-x64`.

```
atmfieldtest.exe                       quick run, about 10 seconds
atmfieldtest.exe --soak-minutes 60     adds a soak run
atmfieldtest.exe --out D:\reports      where the report goes (default: current folder)
atmfieldtest.exe --terminal-id ATM-0042
```

## What it checks
- **Environment:** OS, architecture, memory, free disk, clock, whether it runs elevated.
- **Disk:** fsync latency. Database commits wait for the disk, so this decides transaction speed.
- **Secrets:** creates a DPAPI-protected key and round-trips an encrypted value (Windows).
- **Inventory:** lists file names (never contents) under `C:\ProgramData\KAL`, `C:\ProBase\CONF` and `C:\ProBase\BIN` (add more with `--inventory-dir`), and running processes that look like XFS or device software. Read only.
- **XFS discovery:** lists the logical services and providers registered under `HKLM\SOFTWARE\XFS` (64 and 32 bit views). Read only. If your XFS root is elsewhere pass `--xfs-path "HKLM\SOFTWARE\Vendor\XFS"`; if nothing is found the tool also searches a few levels under `HKLM\SOFTWARE` for a `LOGICAL_SERVICES` key.
- **Platform:** starts the full platform on a temporary folder, runs the health checks and the device self-test against the simulators, 300 transactions with audit entries, verifies the audit chain, 1,000 events on the bus, shutdown.
- **Soak (optional):** a transaction every 100 ms plus periodic health checks, with working set, thread and handle samples every minute, then audit verification.

It writes a `.txt` and a `.json` report and exits 0 (no failures) or 1. Warnings do not fail the run.

## What it does not do
- It does not touch the real devices. The card reader, PIN pad, dispenser and printer are simulators until the Phase 2 XFS adapters exist, so a pass says the platform runs well on that PC, not that the hardware works.
- It uses temporary folders and never reads or writes real ATM data. Run it only on a test machine anyway.
- The 72-hour soak and power-cut tests in the Phase 1 plan still need to be run by hand on the lab machine (`--soak-minutes 4320` covers the soak part).

Please send the `.txt` report back after a run; the XFS section tells us which services Phase 2 has to target.
