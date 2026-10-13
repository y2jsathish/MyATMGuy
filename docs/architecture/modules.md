# Platform services and module guide

Startup is shared by the Host and the integration tests: `PlatformBootstrap.Prepare` (keys, raw config, validated config) then `AddPlatform` (all services). The Host adds WPF only.

| Area | Where | Notes |
|---|---|---|
| Logging | `Infrastructure/Logging` | Serilog behind `ILogger`, async rolling JSON, `MaskingSink` |
| Audit | `Application/Audit`, `Infrastructure/Audit` | HMAC hash chain, DB store with file fallback |
| Config and secrets | `Application/Configuration`, `Infrastructure/Configuration` | layered, validated, hot reload, `enc:` values |
| Persistence | `Infrastructure/Persistence` | EF Core SQLite, repositories, unit of work, migrations, outbox, dead letters |
| Events | `Application/Events` | in-process bounded bus, dead-letter on subscriber fault |
| Health | `Application/Health`, `Infrastructure/Health` | checks feed the banner and monitoring client |
| Notifications | `Application/Notifications` | channels (UI banner, log) |
| Jobs | `Application/Jobs`, `Infrastructure/Jobs` | scheduled maintenance (purge, backup, outbox drain) |
| Resilience | `Infrastructure/Resilience` | Polly v8 pipelines |
| Monitoring | `Infrastructure/Monitoring`, `src/ATM.Monitoring.Api` | heartbeat, health, error reports |
| Supervisor | `Application/Supervisor` | PBKDF2 session |

## Adding a module
Implement `IServiceModule` (register services, optional start/stop hooks), add it to `PlatformServicesModule`. Dependencies must point inward; `ATM.Architecture.Tests` fails the build otherwise.
