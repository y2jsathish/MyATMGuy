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
