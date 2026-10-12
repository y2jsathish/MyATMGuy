# Deployment guide

## Package
CI publishes `atm-host-win-x64` (self-contained, no .NET install needed) from every build. There is no MSI or code signing yet; sign the binaries with the bank's certificate before pilot.

## Target machine
Windows 10/11 IoT Enterprise or LTSC, x64, 8 GB RAM, SSD. Required: BitLocker on the data volume, OS time sync, a dedicated non-admin service/kiosk account, Windows Event Log source created at install time (needs admin once).

## Install
1. Copy the published folder to `C:\ATM\app`.
2. Set the environment: `setx ATM_ENVIRONMENT Prod /M` (Dev, Sit, Uat, Pilot, Prod).
3. Create `terminal.json` with `Terminal:TerminalId`; keep secrets encrypted: `echo -n "value" | atmcfg encrypt`.
4. In Prod write the manifest after the last edit: `atmcfg manifest C:\ATM\app`, then `atmcfg verify C:\ATM\app --env Prod` must pass.
5. First start creates `keys\` (DPAPI-protected), the SQLite database and applies migrations.
6. Restrict write access: only the service account may write `keys\`, `data\`, `logs\`.

## Config precedence
`appsettings.json` < `appsettings.{env}.json` < `terminal.json` < `ATM_` environment variables.

## Monitoring API
Run `ATM.Monitoring.Api` behind HTTPS. Set `Monitoring__ApiKey` (required outside Development). SQLite by default; SQL Server is selectable by config but not yet covered by CI. Contract: `docs/api/monitoring-openapi.json`.

## Smoke test after install
Run `ATM.TestApp.Infrastructure` and `ATM.TestApp.Monitoring` from the test-app bundle, start the Host, open Maintenance, run the device self-test, confirm a heartbeat appears at `GET /api/terminals`.

## Rollback
Keep the previous app folder. Stop the Host, swap folders, restore the database from the pre-upgrade backup (`DatabaseMaintenance` backup/restore, see runbook).
