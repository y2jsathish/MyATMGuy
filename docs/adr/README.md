# Architecture decisions

| ADR | Decision |
|---|---|
| 001 | .NET 8 LTS, C# 12 |
| 002 | WPF with MVVM (CommunityToolkit.Mvvm) |
| 003 | Clean Architecture; dependencies point inward, enforced by NetArchTest |
| 004 | Devices only behind `IDevicePort` interfaces; simulators first, XFS/XFS4IoT adapters in Phase 2 |
| 005 | Generic Host and DI with ValidateOnBuild and ValidateScopes |
| 006 | Central package management, warnings as errors |
| 007 | PAN is only ever held masked (first 6, last 4) |
| 008 | Portable per-layer console test apps alongside xUnit tests |
| 009 | Add `ATM.Presentation` (WPF-free ViewModels) between Application and `ATM.UI`, so ViewModels are testable on any OS. Amends the blueprint folder structure. |
| 010 | Audit trail is an HMAC-SHA256 hash chain with a purge anchor; key derived from key v1 so no extra secret to provision |
| 011 | Secrets are `enc:v{n}:` AES-GCM values; DPAPI key provider on Windows, file key provider only for Dev/Sit |
| 012 | SQLite (WAL, synchronous FULL) on the terminal via EF Core; migrations in source; SQL Server only for the monitoring API |
| 013 | In-process Channels event bus with dead-letter store; no external broker in Phase 1 |
| 014 | Polly v8 resilience pipelines with an outbox for monitoring traffic |
| 015 | Shared `PlatformBootstrap` so the Host and integration tests start the same graph |
| 016 | Transitive package versions pinned centrally (`CentralPackageTransitivePinningEnabled`); vulnerability scan is a CI gate |
| 017 | Authorization host sits behind `IHostGateway` in Application; the withdrawal flow stores the transaction Pending before asking the host and reverses on any failure after a possible approval |
