# Enterprise ATM Platform

Vendor-independent, multi-vendor ATM software platform (.NET 8, WPF, Clean Architecture). Delivery follows the 12-week Phase 1 blueprint in `docs/blueprint`; design in `docs/design`.

## Layers

| Layer | Project | Depends on |
|---|---|---|
| Domain | `src/ATM.Core` | nothing |
| Application | `src/ATM.Application` | Core |
| Infrastructure (device simulators, later XFS/XFS4IoT adapters) | `src/ATM.Infrastructure` | Application, Core |
| Presentation (ViewModels, navigation; WPF free) | `src/ATM.Presentation` | Application, Core |
| UI (WPF views, themes) | `src/ATM.UI` | Presentation |
| Host (composition root, WPF exe) | `src/ATM.Host` | all |

Layer rules are enforced by `tests/ATM.Architecture.Tests`.

## Build and test

```
dotnet test ATM.CrossPlatform.slnf -c Release   # any OS, skips the WPF projects
dotnet build ATM.sln -c Release                 # Windows, includes ATM.UI and ATM.Host
```

## Portable test apps (one per layer)

`tools/ATM.TestApp.{Core,Application,Infrastructure,Presentation}` are plain console apps with no test framework. They run on a bare machine as self-contained single files.

```
build/publish-testapps.sh [win-x64 linux-x64]    # or build/publish-testapps.ps1
artifacts/testapps/<rid>/ATM.TestApp.Core        # prints [PASS]/[FAIL], exit code 0 or 1
artifacts/testapps/<rid>/ATM.TestApp.Core --json # machine readable
```

The UI layer is exercised by running `ATM.Host` itself (Maintenance screen runs the device self-test against the simulators).

## Status

Phase 1 Sprints 1 to 6 are built: foundation, logging and audit, configuration and secrets, persistence, platform services (events, health, jobs, resilience, monitoring client and API), and integration, performance and security hardening. Test counts on the last full run: Core 28, Application 109, Infrastructure 80, Presentation 14, Architecture 8, Monitoring API 10, Integration 13 (9 end to end plus 4 performance). Coverage of Core, Application and Infrastructure: 93% line, 86% branch.

The WPF projects (`ATM.UI`, `ATM.Host`) compile only on Windows, so they are verified by Windows CI. Not done and needing hardware or the bank: 72-hour soak, power-cut tests, lab image install, code signing, MSI, XFS adapters (Phase 2).

Docs: `docs/security/threat-model.md`, `docs/security/pci-dss-mapping.md`, `docs/ops/{deployment-guide,runbook,release-checklist}.md`, `docs/architecture/modules.md`, `docs/adr`.

Test apps: Core, Application, Infrastructure, Presentation, Monitoring (`tools/ATM.TestApp.*`), plus `atmlog` (log viewer) and `atmcfg` (config tool).

## Configuration and secrets (Sprint 3)

Layers, lowest to highest precedence: `appsettings.json`, `appsettings.{ATM_ENVIRONMENT}.json`, `terminal.json`, `ATM_` environment variables (use `__` for nesting, e.g. `ATM_Terminal__TerminalId`). Environments: Dev, Sit, Uat, Pilot, Prod (default Dev). Prod forbids debug features and plaintext secrets, and requires `config.manifest`.

```
echo -n "value" | atmcfg encrypt        # prints enc:v1:... for a config file
atmcfg verify <config-dir> --env Prod   # validity, plaintext secrets, decryptability, integrity manifest
atmcfg manifest <config-dir>            # write SHA-256 manifest after editing config
atmcfg rotate-key
```

Reloadable at runtime (validated first, last known good kept on failure): Logging levels and Terminal idle timeout. Everything else needs a restart. Schema for editors: `docs/config/atm.schema.json`.
