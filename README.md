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

Sprint 1 foundation. Next: logging and audit (Sprint 2), persistence, transaction flows, then XFS adapters (Phase 2).
