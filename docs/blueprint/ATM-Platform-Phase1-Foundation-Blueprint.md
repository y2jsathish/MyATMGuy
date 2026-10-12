# ATM Platform Phase 1 Foundation Blueprint

Oct 12, 2026 · @y2jsathish

## Executive Summary

Phase 1 delivers a production-grade technical foundation for a vendor-independent ATM platform in 12 weeks: six two-week sprints, a team of four, about 388 story points, ending in the Version 1.0 Foundation Release. It does not dispense cash or talk to a host yet. It builds the architecture, logging and audit, configuration, persistence, shared services and delivery pipeline that every later phase (XFS integration, transaction engine, host connectivity, monitoring, software distribution, AI operations) will plug into without rework.

| Item | Value |
| --- | --- |
| Phase | Phase 1, Foundation |
| Duration | 12 weeks, 6 sprints of 2 weeks |
| Team | 1 Solution Architect, 2 Senior .NET Developers, 1 QA Engineer |
| Planned scope | 8 epics, 68 user stories, 388 story points |
| Sprint capacity | 60 to 68 committed points per sprint |
| Release | Version 1.0 Foundation Release (signed installer, documentation, API docs) |
| Exit targets | Unit test coverage above 80%, zero open Critical or High defects, successful deployment on the reference ATM image |
| CI/CD | GitHub Actions (Azure DevOps equivalent noted where relevant) |

**Planning assumptions.** Target runtime is Windows 10/11 IoT Enterprise LTSC on x64 ATM PCs, with the WPF client running full screen in kiosk mode. Week 1 starts on the agreed kickoff date; if the 12 weeks span public holidays, Sprint 5 and Sprint 6 capacity drops and the stretch items listed in Section 5 move to Phase 2 first. Story points use a modified Fibonacci scale (1, 2, 3, 5, 8, 13) sized relative to a reference story (US-101, solution scaffolding, 5 points), with development, review and test effort included in every estimate.

## 1. Project Objectives

Phase 1 succeeds when the platform foundation is built, tested, documented and deployable, so that Phase 2 teams add ATM features rather than infrastructure.

| # | Objective | Measure of success |
| --- | --- | --- |
| O1 | Establish a Clean Architecture solution that isolates business logic from devices, UI and storage | Architecture tests enforce layer rules in CI; Architecture Review Board sign-off at end of Sprint 1 |
| O2 | Provide enterprise logging, audit and error monitoring fit for PCI DSS and bank audit | 100% of security-relevant events audited; no PAN, PIN or track data in any log (automated check) |
| O3 | Deliver a secure, validated, environment-aware configuration engine | Invalid configuration blocks startup with a clear error; all secrets encrypted at rest |
| O4 | Deliver local persistence that survives power loss and supports schema evolution | Migrations run unattended; WAL mode and integrity check pass after forced power-off tests |
| O5 | Provide shared services (event bus, health, notifications, background jobs, error management) through DI | Every service resolvable from the container; health checks report on all core components |
| O6 | Automate build, test, security scanning and packaging | Every merge to main produces a signed, versioned installer with zero manual steps |
| O7 | Ship Version 1.0 Foundation Release | Coverage above 80%, zero Critical or High defects, deployment guide validated on the reference image |
| O8 | Make the codebase ready for multi-vendor XFS integration in Phase 2 | Device abstraction ports defined; a simulated device passes contract tests |

## 2. Functional Scope

Phase 1 builds platform capabilities, not customer-facing ATM transactions. The boundary below is the contract with leadership; anything not in the In Scope table is Phase 2 or later.

**In scope**

| Capability | What Phase 1 delivers |
| --- | --- |
| Solution foundation | .NET 8 solution with ATM.Core, ATM.Application, ATM.Infrastructure, ATM.UI and ATM.Tests; Generic Host; DI composition root |
| UI shell | WPF MVVM shell with navigation, kiosk-mode window, maintenance (supervisor) screen with health view |
| Device abstraction | Port interfaces for card reader, PIN pad, cash dispenser, receipt printer, sensors; a simulator implementation for tests |
| Logging | Serilog structured logging, rolling files, Windows Event Log, sensitive-data masking, correlation IDs |
| Audit | Tamper-evident audit trail stored in SQLite and files, queryable by time, actor, event type |
| Error monitoring | Global exception capture, error catalogue with codes and severity, error persistence |
| Configuration | Layered JSON, environment profiles, validation, DPAPI encryption, runtime reload, last-known-good fallback |
| Persistence | EF Core with SQLite: Transactions, Devices, AuditLogs, Errors, Configuration tables; repositories; migrations; retention |
| Common services | Event bus, health checks, notifications, background jobs, retry and circuit breaker, watchdog |
| Central monitoring stub | ASP.NET Core minimal API receiving heartbeats and health; OpenAPI contract; SQL Server schema |
| Delivery | GitHub Actions CI/CD, signed installer, versioning, deployment guide |

**Out of scope for Phase 1**

- Real XFS/CEN-XFS 3.x or XFS4IoT device integration with Diebold Nixdorf, NCR, Hyosung or GRG hardware (Phase 2)
- Transaction engine, customer screens and journeys (withdrawal, balance, PIN change)
- Host connectivity (ISO 8583, NDC/DDC emulation) and key exchange with the host HSM
- Production central monitoring portal, remote software distribution, AI predictive maintenance
- PCI PTS and EMV Level 2 certification activities (the architecture prepares for them)

## 3. Non-Functional Requirements

Each NFR has a measurable target and a named verification step so QA can prove it before release.

| ID | Category | Requirement | Target | Verified by |
| --- | --- | --- | --- | --- |
| NFR-01 | Performance | Cold start to ready-for-service screen | 10 s or less on reference ATM PC | Automated startup benchmark, Sprint 6 |
| NFR-02 | Performance | Log write overhead per event (async sink) | Under 1 ms on caller thread at p95 | BenchmarkDotNet |
| NFR-03 | Performance | SQLite insert of a transaction record | Under 20 ms at p95 | Integration benchmark |
| NFR-04 | Performance | Event bus publish to handler dispatch | Under 5 ms at p95, 1,000 events/s sustained | Load test |
| NFR-05 | Resource | Steady-state memory of client process | Under 300 MB; no growth over a 72-hour soak | Soak test |
| NFR-06 | Reliability | Survive abrupt power loss without data corruption | 50 of 50 forced power-off cycles pass integrity check | Power-cut test in lab |
| NFR-07 | Reliability | Unhandled exceptions captured and logged | 100% captured; app restarts via watchdog | Fault-injection tests |
| NFR-08 | Availability | Client designed for 24x7 operation | Background jobs never block UI thread; graceful degradation | Code review, soak test |
| NFR-09 | Security | No sensitive cardholder data in logs, DB or config in clear text | Zero findings from masking test suite and DB scan | Automated tests, security review |
| NFR-10 | Security | Secrets encrypted at rest | DPAPI machine scope; zero plaintext secrets in repo or config | Secret scanning, config audit |
| NFR-11 | Auditability | Audit records are tamper-evident and retained | HMAC hash chain verified; retention configurable (default 90 days local) | Chain verification test |
| NFR-12 | Maintainability | Unit test coverage of Core and Application | Above 80% line coverage, above 70% branch | Coverlet gate in CI |
| NFR-13 | Maintainability | Static analysis clean | Zero analyzer errors; warnings treated as errors | CI build |
| NFR-14 | Portability | Vendor independence | No vendor SDK referenced outside Infrastructure device adapters | Architecture tests |
| NFR-15 | Operability | Health of every core component visible | Health checks for DB, disk, config, memory, event bus, devices | Health endpoint and maintenance screen |
| NFR-16 | Deployability | Unattended install and upgrade | Silent install under 5 minutes; rollback to previous version | Install tests on reference image |
| NFR-17 | Compliance | Alignment with PCI DSS v4.0 controls relevant to software | Control mapping documented and reviewed | Security review, Sprint 6 |
| NFR-18 | Accessibility | Prepare for ADA and EN 301 549 compliance in Phase 2 | UI shell supports high contrast, font scaling and audio hooks | UI review |

## 4. Architecture Overview

The platform follows Clean Architecture: dependencies point inward to ATM.Core, so vendor devices, databases, hosts and UI frameworks can be swapped without touching business rules. This is what makes a KAL-style multi-vendor product possible.

```text
+-----------------------------------------------------------------------------------+
|                               ATM TERMINAL (Windows IoT)                          |
|                                                                                   |
|  +-----------------------------+      +----------------------------------------+  |
|  |  ATM.UI  (WPF, MVVM)        |      |  ATM.Host (Generic Host bootstrapper)  |  |
|  |  Shell / Navigation         |<-----|  DI composition root, Serilog, Options |  |
|  |  Maintenance & Health views |      |  Hosted services, Watchdog             |  |
|  +-------------+---------------+      +-------------------+--------------------+  |
|                | commands / queries                       | registers             |
|                v                                          v                       |
|  +-----------------------------------------------------------------------------+  |
|  |  ATM.Application  (use cases, CQRS handlers, validation, ports)             |  |
|  |  IAuditService  IConfigurationService  IEventBus  IHealthService            |  |
|  |  INotificationService  IBackgroundJobScheduler  IUnitOfWork  IDevicePort    |  |
|  +-------------------------------------+---------------------------------------+  |
|                                        | depends on                               |
|                                        v                                          |
|  +-----------------------------------------------------------------------------+  |
|  |  ATM.Core  (domain: entities, value objects, domain events, error codes)    |  |
|  |  Transaction  Device  AuditEntry  ErrorRecord  ConfigItem  TerminalId       |  |
|  |  NO references to UI, EF Core, Serilog, XFS, or any vendor SDK              |  |
|  +-----------------------------------------------------------------------------+  |
|                                        ^ implements ports                         |
|  +-------------------------------------+---------------------------------------+  |
|  |  ATM.Infrastructure                                                         |  |
|  |  Persistence: EF Core + SQLite (WAL), Repositories, Migrations              |  |
|  |  Logging: Serilog sinks (File, EventLog, SQLite), Masking enricher          |  |
|  |  Security: DPAPI protector, HMAC audit chain, IKeyProvider                  |  |
|  |  Messaging: Channel-based EventBus      Resilience: Polly policies          |  |
|  |  Devices: Simulator adapters  (Phase 2: XFS / XFS4IoT vendor adapters)      |  |
|  |  Http: Monitoring API client (heartbeat, health)                            |  |
|  +--------------+-------------------------------+-------------------------------+  |
|                 |                               |                                 |
|          +------v------+                 +------v-------+                         |
|          |  SQLite DB  |                 | Log files +  |                         |
|          |  atm.db     |                 | Windows Event|                         |
|          +-------------+                 |  Log         |                         |
|                                          +--------------+                         |
+-------------------------------------------------+---------------------------------+
                                                  | HTTPS (TLS 1.2+, mTLS in Phase 2)
                                                  v
                         +-------------------------------------------+
                         |  CENTRAL MONITORING (Phase 1 stub)        |
                         |  ATM.Monitoring.Api (ASP.NET Core 8)      |
                         |  Heartbeat, Health, Error intake (REST)   |
                         |  SQL Server: Terminals, Heartbeats, Errors|
                         +-------------------------------------------+

  PHASE 2+ PLUG-IN POINTS:  XFS adapters (DN, NCR, Hyosung, GRG) -> IDevicePort
                            Host connector (ISO 8583 / NDC)      -> IHostGateway
                            Software distribution agent          -> IUpdateAgent
                            AI ops telemetry pipeline            -> IEventBus
```

**Layer rules (enforced by architecture tests in CI)**

| Layer | May reference | Must never reference |
| --- | --- | --- |
| ATM.Core | Nothing beyond the .NET base library | EF Core, Serilog, WPF, HTTP, vendor SDKs |
| ATM.Application | ATM.Core, Microsoft.Extensions abstractions | Infrastructure, UI, concrete loggers or DB providers |
| ATM.Infrastructure | ATM.Application, ATM.Core, third-party libraries | ATM.UI |
| ATM.UI | ATM.Application (via DI), CommunityToolkit.Mvvm | ATM.Infrastructure types directly (only the composition root wires them) |
| ATM.Host | All projects | Business logic of any kind |

**Key architecture decisions (ADRs to be ratified in Sprint 1)**

1. ADR-001 Clean Architecture with ports and adapters; ATM.Core has zero external dependencies.
2. ADR-002 Microsoft.Extensions.Hosting Generic Host inside WPF for DI, configuration, logging and hosted services.
3. ADR-003 CommunityToolkit.Mvvm for MVVM source generators instead of a heavyweight framework.
4. ADR-004 EF Core 8 with SQLite locally (WAL mode) and SQL Server centrally, behind repositories and Unit of Work.
5. ADR-005 In-process event bus built on System.Threading.Channels; designed so a broker can replace it centrally later.
6. ADR-006 Device access only through IDevicePort abstractions so XFS 3.x and XFS4IoT adapters for each vendor are plug-ins.
7. ADR-007 Serilog with compact JSON as the canonical log format to keep logs machine-searchable.
8. ADR-008 DPAPI (machine scope) for secrets in Phase 1, behind IKeyProvider so TPM or HSM-backed keys can follow.

## 5. Sprint Roadmap and Capacity Planning

The six sprints build the platform bottom-up, each ending in a demonstrable increment and a sprint gate. Committed load stays between 61 and 68 points, inside a calculated capacity of about 70.

```text
 Sprint 1        Sprint 2        Sprint 3        Sprint 4        Sprint 5        Sprint 6
 Weeks 1-2       Weeks 3-4       Weeks 5-6       Weeks 7-8       Weeks 9-10      Weeks 11-12
 Foundation      Logging         Configuration   Database        Common          Integration
 Architecture    and Audit       Framework       Layer           Services        MVP Release
 61 pts          64 pts          66 pts          68 pts          68 pts          61 pts
    <>              <>              <>              <>              <>              <>
 Architecture    Audit           Config          CRUD,           Health,         v1.0
 approved        recorded        encrypted       migrations      pub/sub         Go/No-Go
```

Each sprint builds on the one before it: logging and audit come first because every later module writes to them, and the release sprint is held lighter for hardening.

| Sprint | Weeks | Theme | Sprint goal | Points | Gate at sprint end |
| --- | --- | --- | --- | --- | --- |
| S1 | 1 to 2 | Foundation and Architecture | Clean Architecture solution builds in CI with DI, MVVM shell and standards enforced | 61 | Architecture approved by ARB; CI green |
| S2 | 3 to 4 | Logging and Audit | Every event is logged, masked, correlated and audited in tamper-evident form | 64 | Audit logs recorded; errors auto-captured; logs searchable |
| S3 | 5 to 6 | Configuration | Validated, encrypted, environment-aware configuration loads and reloads at runtime | 66 | Dynamic load, validation and encryption demonstrated |
| S4 | 7 to 8 | Database Layer | All five tables persist through repositories with unattended migrations | 68 | CRUD, migrations and local transaction storage operational |
| S5 | 9 to 10 | Common Services | Event bus, health, notifications and background jobs run through DI | 68 | Pub/sub, DI-managed services, health checks working |
| S6 | 11 to 12 | Integration and MVP Release | Version 1.0 Foundation Release is tested, secured, documented and packaged | 61 | Coverage above 80%, zero Critical bugs, deployable package |
| Total | 12 |  |  | 388 | Release Go/No-Go |

**Capacity model (per two-week sprint)**

| Role | Available days | Ceremonies and overhead | Focus days | Share on backlog | Backlog days |
| --- | --- | --- | --- | --- | --- |
| Solution Architect | 10 | 2.0 (incl. ARB, stakeholder reviews) | 8.0 | 50% hands-on | 4.0 |
| Senior .NET Developer 1 | 10 | 1.5 | 8.5 | 100% | 8.5 |
| Senior .NET Developer 2 | 10 | 1.5 | 8.5 | 100% | 8.5 |
| QA Engineer | 10 | 1.5 | 8.5 | 100% (tests, automation) | 8.5 |
| Team total | 40 | 6.5 | 33.5 |  | 29.5 |

At about 2.4 points per backlog day (the initial planning assumption for a senior team new to this codebase, with QA effort inside each story estimate), capacity is about 70 points. The plan commits 61 to 68, leaving a 3% to 13% buffer for production-grade rework, ARB feedback and unplanned support. Sprint 1 is set lower for team ramp-up; Sprint 6 is set lower because release activities, bug fixing and documentation review are hard to estimate.

**Capacity rules**

- Velocity is re-baselined after Sprint 2 using the actual average of Sprints 1 and 2; later sprint commitments are adjusted, never the Definition of Done.
- Each sprint reserves at least 10% for defects and refactoring; unused buffer goes to the stretch items below.
- Stretch items, in order if capacity frees up: SQLCipher full-database encryption spike (5), health dashboard trend charts (3), log shipping to central monitoring (5). These are the first to move to Phase 2 if capacity drops.
- Public holidays and leave are subtracted at sprint planning; the Scrum Master role is held by the Solution Architect with QA as backup facilitator.

## 6. Product Backlog: Epics and Features

The backlog holds 8 epics, 24 features and 68 user stories totalling 388 points. Stories are listed by sprint in Sections 7 and 8, each with its estimate and acceptance criteria.

| Epic | Name | Features | Sprints | Points |
| --- | --- | --- | --- | --- |
| EP-01 | Solution Foundation and Architecture | F-01 Solution scaffolding; F-02 Architecture governance; F-03 Device abstraction ports | S1 | 28 |
| EP-02 | UI Shell (WPF MVVM) | F-04 Shell and navigation; F-05 Maintenance and health views | S1, S5 | 13 |
| EP-03 | DevOps and Engineering Standards | F-06 CI pipeline; F-07 Standards and analyzers; F-08 Versioning and packaging | S1, S3, S6 | 36 |
| EP-04 | Logging, Audit and Error Monitoring | F-09 Structured logging; F-10 Audit trail; F-11 Error monitoring | S2 | 64 |
| EP-05 | Configuration Management | F-12 Layered configuration; F-13 Secure configuration; F-14 Runtime configuration | S3 | 63 |
| EP-06 | Data Persistence | F-15 SQLite and EF Core; F-16 Repositories; F-17 Migrations and data lifecycle | S4 | 68 |
| EP-07 | Common Services Framework | F-18 Event bus; F-19 Health monitoring; F-20 Notifications and background jobs; F-21 Resilience; F-22 Central monitoring stub | S5 | 63 |
| EP-08 | Quality, Security and Release | F-23 Verification and hardening; F-24 Release and documentation | S6 | 53 |
| Total |  | 24 features |  | 388 |

**Prioritisation.** Priority uses MoSCoW: every story below is a Must unless marked (S) for Should. Should stories are the first candidates to move if velocity is below plan after Sprint 2. Backlog refinement happens weekly; stories enter a sprint only when they meet the Definition of Ready in Section 10.

## 7. User Stories and 8. Acceptance Criteria

Personas: **Platform Developer** (builds on the framework), **ATM Operations Engineer** (installs and supports terminals), **Security Officer** (bank information security and PCI), **Auditor** (internal or external audit), **Supervisor** (branch staff using the maintenance screen), **Release Manager**.

### Sprint 1: Foundation and Architecture (61 points)

| ID | User story | Pts | Acceptance criteria |
| --- | --- | --- | --- |
| US-101 | As a Platform Developer, I want a .NET 8 solution with ATM.Core, ATM.Application, ATM.Infrastructure, ATM.UI, ATM.Host and ATM.Tests, so that every team works in one agreed structure | 5 | Solution builds with dotnet build on a clean machine; target net8.0-windows for UI and net8.0 elsewhere; central package management in Directory.Packages.props |
| US-102 | As a Solution Architect, I want layer dependency rules enforced by tests, so that Clean Architecture cannot erode | 5 | NetArchTest suite fails the build if Core references any other project or package; rules from Section 4 all covered |
| US-103 | As a Platform Developer, I want a DI composition root using the Generic Host, so that services, options and logging are wired in one place | 8 | App starts via Host.CreateApplicationBuilder; each layer exposes an AddXxx extension; container validated with ValidateOnBuild and ValidateScopes |
| US-104 | As a Supervisor, I want a full-screen WPF shell with navigation, so that future screens plug in consistently | 8 | MVVM with CommunityToolkit.Mvvm; INavigationService switches views by ViewModel; no code-behind logic beyond InitializeComponent; kiosk mode configurable |
| US-105 | As a Tech Lead, I want coding standards enforced by tooling, so that reviews focus on design not style | 5 | .editorconfig, Roslyn analyzers and StyleCop active; TreatWarningsAsErrors on; nullable enabled; dotnet format check passes in CI |
| US-106 | As a Release Manager, I want a GitHub Actions CI pipeline, so that every PR is built, tested and analysed automatically | 8 | Pipeline runs on PR and main; restore, build, test, coverage report, analyzers; fails on any test or analyzer error; run time under 10 minutes |
| US-107 | As a Tech Lead, I want branch protection, PR templates and CODEOWNERS, so that no unreviewed code reaches main | 3 | main protected; 1 approving review plus code owner; status checks required; linear history; PR template in repo |
| US-108 | As a Bank Architecture Review Board member, I want the architecture document and ADRs, so that I can approve the design | 5 | ADR-001 to ADR-008 written; architecture document reviewed; ARB approval recorded |
| US-109 | As a Platform Developer, I want device port interfaces and a simulator, so that Phase 2 XFS adapters plug in without changing Application code | 5 | IDevicePort, ICardReaderPort, IPinPadPort, ICashDispenserPort, IReceiptPrinterPort defined in Application; simulator in Infrastructure passes contract tests |
| US-110 | As a new Developer, I want a documented environment setup and bootstrap script, so that I am productive on day one | 3 | README setup guide; bootstrap.ps1 installs SDK checks and tools; new member builds and runs tests in under 1 hour |
| US-111 | As a QA Engineer, I want the test framework stack configured, so that tests follow one pattern | 3 | xUnit, FluentAssertions, NSubstitute, coverlet, Verify configured; sample tests per layer; test naming convention documented |
| US-112 | As a Release Manager, I want automatic semantic versioning and build artifacts, so that every build is traceable | 3 | MinVer or GitVersion stamps assembly and file versions from Git tags; build artifacts retained 30 days |

### Sprint 2: Logging and Audit Framework (64 points)

| ID | User story | Pts | Acceptance criteria |
| --- | --- | --- | --- |
| US-201 | As an ATM Operations Engineer, I want structured logs enriched with terminal ID, version, machine and correlation ID, so that I can trace any event | 8 | Serilog configured via host; enrichers present on every event; message templates used (no string interpolation), checked by analyzer |
| US-202 | As an ATM Operations Engineer, I want rolling log files with retention, so that disks never fill | 3 | Daily roll, 50 MB size cap per file, 30 files retained (configurable); async sink; disk-full test shows no crash |
| US-203 | As an ATM Operations Engineer, I want warnings and errors written to the Windows Event Log, so that existing enterprise monitoring picks them up | 5 | Warning and above go to Application log under source ATMPlatform; event IDs mapped from error catalogue; source registered by installer |
| US-204 | As a Security Officer, I want PAN, PIN, track data, CVV and secrets masked in every log, so that logs are out of PCI scope for cardholder data | 8 | Masking enricher and destructuring policy; PAN shown as first 6 and last 4 at most; 30+ test patterns pass; masking cannot be disabled by config |
| US-205 | As an Auditor, I want an audit trail of who did what, when, where and with what outcome, so that I can reconstruct any action | 8 | IAuditService records actor, action, target, timestamp (UTC), terminal, outcome, correlation ID; audit categories defined (security, config, operator, system) |
| US-206 | As a Security Officer, I want audit records to be tamper-evident, so that any edit or deletion is detectable | 8 | Each record carries HMAC-SHA256 of its content plus previous hash; verifier detects altered, deleted or reordered records in tests |
| US-207 | As an ATM Operations Engineer, I want all unhandled exceptions captured, so that no failure is silent | 5 | Handlers on AppDomain, Dispatcher, TaskScheduler; exception logged with context then the app shuts down cleanly or recovers per policy |
| US-208 | As an ATM Operations Engineer, I want an error catalogue with codes and severity, so that errors are consistent and countable | 5 | ErrorCode registry in Core (ATM-xxxx); severity levels; error counters exposed for health checks |
| US-209 | As an ATM Operations Engineer, I want searchable logs, so that I can find events by time, level, terminal or correlation ID | 8 | Compact JSON log format; log viewer utility filters by time range, level, correlation ID and text; 1 GB of logs searched in under 10 s |
| US-210 | As a Platform Developer, I want correlation IDs to flow across async calls, so that one operation can be followed end to end | 3 | AsyncLocal-based context; propagated into event bus and HTTP headers; test proves continuity across await boundaries |
| US-211 | As an ATM Operations Engineer, I want log levels set per namespace in configuration, so that I can raise detail without a redeploy | 3 | MinimumLevel overrides read from configuration; change applied at runtime via level switch |

### Sprint 3: Configuration Framework (66 points)

| ID | User story | Pts | Acceptance criteria |
| --- | --- | --- | --- |
| US-301 | As a Platform Developer, I want layered JSON configuration, so that defaults, environment and terminal settings combine predictably | 8 | Order: appsettings.json, appsettings.{Environment}.json, terminal.json, environment variables; precedence covered by tests |
| US-302 | As an ATM Operations Engineer, I want environment profiles (Dev, SIT, UAT, Pilot, Prod), so that one build runs everywhere | 5 | Environment selected by ATM\_ENVIRONMENT; profile shown on maintenance screen; Prod forbids debug features |
| US-303 | As a Platform Developer, I want strongly typed, validated options, so that bad configuration fails fast | 8 | Options classes with DataAnnotations or FluentValidation; ValidateOnStart; startup blocked with a clear audited error listing each invalid key |
| US-304 | As a Security Officer, I want sensitive settings encrypted at rest, so that secrets are not readable on disk | 8 | Values marked enc: are DPAPI machine-scope encrypted; decrypted only in memory; plaintext secret at startup in Prod is rejected |
| US-305 | As a Solution Architect, I want an IKeyProvider abstraction, so that keys can later move to TPM or HSM | 5 | Interface in Application; DPAPI implementation; key version stored with ciphertext; rotation procedure documented |
| US-306 | As an ATM Operations Engineer, I want configuration reloaded at runtime, so that safe changes apply without restart | 8 | IOptionsMonitor used for reloadable sections; reload validated before applying; non-reloadable sections documented |
| US-307 | As a Security Officer, I want configuration integrity verified, so that tampered files are detected | 5 | Config files signed or hashed; mismatch raises security audit event and falls back per US-310 |
| US-308 | As an ATM Operations Engineer, I want a command-line tool to encrypt values, so that I can prepare secure config | 5 | atmcfg encrypt and verify commands; runs on target machine; never echoes plaintext |
| US-309 | As an Auditor, I want every configuration change audited, so that changes are attributable | 3 | Old and new value hashes (not values for secrets), source and timestamp written to audit trail |
| US-310 | As an ATM Operations Engineer, I want a last-known-good fallback, so that a bad change never takes a terminal down | 5 | Valid config snapshot kept; invalid reload reverts to it and raises a High error |
| US-311 | As a Platform Developer, I want a JSON schema for configuration, so that editors validate files before deployment (S) | 3 | Schema generated from options classes; validated in CI against sample files |
| US-312 | As a Security Officer, I want secret scanning in the pipeline, so that credentials never enter the repository | 3 | Gitleaks (or GitHub secret scanning with push protection) blocks the PR on detection |

### Sprint 4: Database Layer (68 points)

| ID | User story | Pts | Acceptance criteria |
| --- | --- | --- | --- |
| US-401 | As a Platform Developer, I want EF Core 8 with SQLite configured for ATM conditions, so that local data is durable | 8 | WAL journal mode, synchronous=FULL, busy timeout set; single DbContext factory; connection string from validated options |
| US-402 | As a Platform Developer, I want entities and mappings for Transactions, Devices, AuditLogs, Errors and Configuration, so that core data has a home | 8 | Fluent API mappings in Infrastructure; Core entities free of EF attributes; UTC timestamps; row version on mutable tables |
| US-403 | As a Platform Developer, I want a generic repository and Unit of Work, so that persistence is consistent and testable | 5 | IRepository and IUnitOfWork in Application; transactions committed atomically; no IQueryable leaks to Application |
| US-404 | As a Platform Developer, I want specific repositories with query methods, so that use cases read data efficiently | 8 | Transaction, Device, Audit, Error, Configuration repositories; paging; indexes on timestamp, terminal and correlation columns |
| US-405 | As an ATM Operations Engineer, I want migrations applied automatically and safely, so that upgrades need no manual SQL | 8 | EF migrations bundle; database backed up before migrate; failed migration restores backup and blocks startup with clear error |
| US-406 | As a Security Officer, I want cardholder data protected in the database, so that a stolen disk exposes nothing | 8 | No full PAN stored (masked or tokenised only); sensitive columns encrypted with IKeyProvider keys; DB scan test finds no PAN patterns |
| US-407 | As an ATM Operations Engineer, I want data retention and purge, so that the database stays bounded | 5 | Retention per table from configuration (defaults: transactions 90 days, errors 30 days, audit 365 days); purge audited |
| US-408 | As an ATM Operations Engineer, I want database integrity checks and recovery, so that corruption is detected and handled | 5 | PRAGMA integrity\_check at startup; on failure restore last backup and raise Critical error; nightly backup job |
| US-409 | As an Auditor, I want audit and error records persisted to the database, so that they are queryable alongside transactions | 5 | Audit writer and error sink write to AuditLogs and Errors; hash chain preserved; write failure falls back to file |
| US-410 | As a Solution Architect, I want the SQL Server provider proven behind the same abstractions, so that central services reuse the data layer | 5 | Monitoring stub schema migrates on SQL Server (LocalDB in CI); provider chosen by configuration |
| US-411 | As a QA Engineer, I want repository integration tests on real SQLite, so that SQL behaviour is verified | 3 | Tests use file-based SQLite per test; cover CRUD, paging, concurrency conflicts and migrations up from empty |

### Sprint 5: Common Services Framework (68 points)

| ID | User story | Pts | Acceptance criteria |
| --- | --- | --- | --- |
| US-501 | As a Platform Developer, I want an in-process event bus, so that modules communicate without direct references | 8 | IEventBus publish and subscribe; async handlers via Channels; handler failure isolated from other handlers; ordering per event type preserved |
| US-502 | As a Platform Developer, I want versioned event contracts, so that events evolve without breaking subscribers | 3 | Events are immutable records with version and correlation ID; contract tests guard serialisation |
| US-503 | As an ATM Operations Engineer, I want health checks for each core component, so that I know the terminal state | 8 | Checks for DB, disk space, configuration, memory, event bus backlog and device simulator; Healthy, Degraded, Unhealthy states; run on schedule |
| US-504 | As a Supervisor, I want a health view on the maintenance screen, so that I can see problems without tools | 5 | Maintenance view lists checks with state, last run and message; refreshes every 30 s; access requires supervisor mode |
| US-505 | As a Platform Developer, I want a notification service, so that alerts reach the right channel | 5 | INotificationService with channels for UI banner, log and monitoring API; severity routing from configuration |
| US-506 | As a Platform Developer, I want background jobs managed by the host, so that periodic work is reliable | 8 | Hosted services with cron-style schedules (purge, backup, heartbeat); jobs stop gracefully on shutdown; overlapping runs prevented |
| US-507 | As an ATM Operations Engineer, I want retries and circuit breakers, so that transient faults recover automatically | 8 | Polly v8 resilience pipelines registered by name; retry with jitter, timeout, circuit breaker; breaker state changes logged and published as events |
| US-508 | As an ATM Operations Engineer, I want the terminal to send heartbeats and health to central monitoring, so that the estate is visible | 5 | REST client sends heartbeat every 60 s over HTTPS; offline queue persists and replays; uses resilience pipeline |
| US-509 | As a Platform Developer, I want a central monitoring stub API with OpenAPI, so that the contract is fixed early | 5 | ASP.NET Core minimal API: POST heartbeat, POST health, POST error, GET terminals; OpenAPI spec published; SQL Server storage |
| US-510 | As a Platform Developer, I want module registration conventions, so that new modules register services consistently | 5 | IServiceModule pattern; lifetimes documented; container validation catches captive dependencies |
| US-511 | As an ATM Operations Engineer, I want a watchdog and graceful shutdown, so that a hung client recovers | 5 | Watchdog service restarts the client if UI heartbeat stops for 60 s; shutdown flushes logs and DB within 10 s |
| US-512 | As a Platform Developer, I want failed events dead-lettered, so that nothing is lost silently (S) | 3 | Events failing after retries stored in a dead-letter table with error; replay command available |

### Sprint 6: Integration and MVP Release (61 points)

| ID | User story | Pts | Acceptance criteria |
| --- | --- | --- | --- |
| US-601 | As a QA Engineer, I want an end-to-end integration suite, so that the framework is proven working together | 8 | Scenarios cover startup, config load and reload, DB write, event publish, audit chain, health report and heartbeat; all run in CI |
| US-602 | As a Tech Lead, I want unit test coverage above 80%, so that the release target is met | 8 | Line coverage above 80% and branch above 70% for Core, Application and Infrastructure; gate enforced in CI |
| US-603 | As an ATM Operations Engineer, I want performance validated against NFRs, so that the platform runs on ATM hardware | 8 | NFR-01 to NFR-05 measured on reference PC; 72-hour soak test with no memory growth; report published |
| US-604 | As a Security Officer, I want a security review, so that the release is safe to pilot | 8 | STRIDE threat model; SAST (CodeQL) and dependency scan with zero Critical or High open; PCI DSS control mapping reviewed |
| US-605 | As a Release Manager, I want a signed installer, so that terminals install and upgrade unattended | 8 | WiX MSI (or MSIX) signed with bank code-signing certificate; silent install, upgrade and rollback tested |
| US-606 | As an ATM Operations Engineer, I want a deployment guide, so that I can install without the developers | 3 | Guide covers prerequisites, install, configuration, encryption, verification and rollback; validated by someone outside the team |
| US-607 | As a Platform Developer, I want technical documentation, so that Phase 2 teams onboard quickly | 5 | Architecture document, ADRs, module guides and runbook in docs folder; reviewed by architect |
| US-608 | As a Platform Developer, I want API documentation, so that public interfaces are discoverable | 5 | XML docs on all public types; DocFX site generated in CI; OpenAPI for monitoring API |
| US-609 | As a Release Manager, I want a release candidate hardening window, so that defects are closed before Go/No-Go | 5 | RC build frozen; all Critical and High defects fixed and retested; regression suite green |
| US-610 | As an ATM Operations Engineer, I want the release installed on a lab ATM image, so that it is proven on real target conditions | 3 | Installed on Windows IoT Enterprise LTSC lab image; smoke test passes; evidence attached to release record |

## 9. Technical Tasks

Every story is broken into tasks at sprint planning; the table lists the standard tasks per sprint so estimates and owners are clear from day one.

| Sprint | Technical tasks |
| --- | --- |
| S1 | Create repo and solution; Directory.Build.props and Directory.Packages.props; add projects and references; Generic Host bootstrap in App.xaml.cs; ServiceCollection extensions per layer; MVVM shell, ViewModelLocator-free navigation; NetArchTest rules; .editorconfig and analyzers; GitHub Actions build-test workflow; branch protection and CODEOWNERS; ADR template and ADRs 001 to 008; device ports and simulator; bootstrap.ps1 |
| S2 | Serilog packages and configuration; enrichers (terminal, version, correlation); file and EventLog sinks; masking enricher and destructuring policies with regex and Luhn check; AuditEntry entity and IAuditService; HMAC chain and verifier; global exception handlers; ErrorCode registry; CLEF format and log viewer tool; LoggingLevelSwitch wiring |
| S3 | Configuration builder layering; options classes per module; validators and ValidateOnStart; DPAPI ProtectedConfigurationProvider; IKeyProvider; IOptionsMonitor reload with pre-validation; config hash or signature check; atmcfg CLI; config change audit hook; last-known-good snapshot; JSON schema generation; gitleaks step in CI |
| S4 | EF Core and SQLite packages; DbContext and factory; entity configurations; repositories and Unit of Work; initial migration and migration bundle; pre-migration backup and restore; column encryption value converters; retention purge job; integrity check and nightly backup; SQL Server provider and LocalDB in CI; integration test fixtures |
| S5 | Channel-based EventBus; event contracts; IHealthCheck implementations and scheduler; maintenance health view; INotificationService and channels; hosted job scheduler; Polly resilience pipelines; heartbeat client with offline queue; monitoring minimal API with Swagger; IServiceModule conventions; watchdog service; dead-letter store |
| S6 | E2E scenario tests; coverage gap analysis and tests; BenchmarkDotNet and soak test harness; threat model workshop; CodeQL and dependency review triage; WiX installer and signing step; deployment guide; DocFX site; release notes; RC freeze and regression; lab install and evidence |

## 10. Definition of Ready and Definition of Done

A story enters a sprint only when Ready and counts toward velocity only when Done; partially done work earns zero points.

**Definition of Ready (story level)**

- [ ] Written as a user story with a clear persona and business value
- [ ] Acceptance criteria are testable and agreed by Product Owner and QA
- [ ] Estimated by the team; 13 points or more must be split
- [ ] Dependencies, security impact and NFRs identified
- [ ] Design notes or ADR reference attached where architecture is affected

**Definition of Done (story level)**

- [ ] Code complete, follows coding standards, zero analyzer warnings
- [ ] Unit tests written and passing; new code coverage at least 80%
- [ ] Integration tests added where the story touches I/O, DB or the host
- [ ] Peer reviewed and approved via PR by at least one other engineer plus code owner
- [ ] CI pipeline green, including architecture tests, secret scan and SAST
- [ ] No sensitive data logged, persisted or committed (masking tests pass)
- [ ] Acceptance criteria verified by QA on the integration build
- [ ] Public APIs have XML documentation; configuration keys documented
- [ ] Security-relevant actions audited
- [ ] Product Owner accepted at or before sprint review

**Definition of Done (sprint level)**

- [ ] Sprint goal met and increment deployable from main
- [ ] Regression suite green on the sprint build
- [ ] No open Critical or High defects introduced in the sprint
- [ ] Sprint demo held and gate criteria from Section 5 signed off
- [ ] Documentation and ADRs updated; release notes drafted

**Definition of Done (release level)**: all items in Section 20, the Release Readiness Checklist.

## 11. Risk Register and Mitigation

The two highest risks are a four-person team losing a member and late security findings; both are mitigated by pairing and by shifting security checks into every sprint. Scores use Probability (1 to 5) times Impact (1 to 5); 15 or above is reviewed weekly with leadership.

| ID | Risk | P | I | Score | Mitigation | Contingency | Owner |
| --- | --- | --- | --- | --- | --- | --- | --- |
| R-01 | Loss or long absence of one of four team members | 3 | 5 | 15 | Pair programming on core modules; documentation in DoD; cross-training rotation each sprint | Move Should stories to Phase 2; request contractor backfill | Solution Architect |
| R-02 | Security or PCI findings arrive late in Sprint 6 | 3 | 5 | 15 | SAST, secret and dependency scans from Sprint 1; security checklist in DoR; Security Officer at Sprint 2 and 4 reviews | Release as pilot-only with risk acceptance signed by CISO | Solution Architect |
| R-03 | Bank ARB approval of architecture delayed beyond Sprint 1 | 3 | 4 | 12 | Pre-review ADRs with ARB in week 1; book ARB slot before kickoff | Proceed on provisional approval for reversible decisions only | Solution Architect |
| R-04 | Velocity below plan in early sprints | 3 | 4 | 12 | Sprint 1 lower load; re-baseline after Sprint 2; Should stories as buffer | Descope stretch and Should items | Scrum Master |
| R-05 | Coverage target of 80% not reached | 2 | 4 | 8 | Coverage gate on new code from Sprint 1; QA tracks trend per sprint | Dedicated coverage story moved earlier | QA Engineer |
| R-06 | SQLite corruption under power loss on ATM hardware | 2 | 5 | 10 | WAL, synchronous FULL, integrity check, backups; power-cut tests in Sprint 4 | Restore from backup; raise priority of SQLCipher and journaling review | Senior Dev 1 |
| R-07 | Reference ATM hardware or lab image not available | 3 | 4 | 12 | Request lab PC and Windows IoT LTSC image at kickoff; use VM image meanwhile | Validate on equivalent industrial PC; schedule lab test in Phase 2 week 1 | Solution Architect |
| R-08 | Code-signing certificate not issued in time | 3 | 3 | 9 | Raise request in Sprint 3; test with internal CA cert | Release internally signed for lab and pilot only | Release Manager (Architect) |
| R-09 | Phase 2 XFS needs not reflected in Phase 1 abstractions | 2 | 4 | 8 | Device ports reviewed against XFS 3.x and XFS4IoT class models in Sprint 1 | Version port interfaces; adapter shims | Solution Architect |
| R-10 | Third-party package vulnerability or licence issue | 2 | 3 | 6 | Approved package list; Dependabot; licence check in CI | Replace package; pin patched version | Senior Dev 2 |
| R-11 | WPF on .NET 8 kiosk behaviour issues (touch, focus, DPI) | 2 | 3 | 6 | Spike on target display in Sprint 1; per-monitor DPI awareness | Fallback window settings in configuration | Senior Dev 2 |
| R-12 | Scope creep from stakeholders wanting ATM features early | 4 | 3 | 12 | Scope contract in Section 2; change requests via Product Owner only | Log for Phase 2 backlog | Product Owner |
| R-13 | Holiday periods reduce capacity in later sprints | 3 | 3 | 9 | Leave plan agreed at kickoff; capacity recalculated each planning | Shift stretch items; protect Sprint 6 release work | Scrum Master |

## 12. Deliverables

Each sprint hands over working software plus the evidence leadership needs to sign its gate.

| Sprint | Software deliverables | Documentation and evidence |
| --- | --- | --- |
| S1 | ATM.Core, ATM.Application, ATM.Infrastructure, ATM.UI, ATM.Host, ATM.Tests; DI composition root; MVVM shell; device ports and simulator; CI pipeline | Architecture document; ADR-001 to ADR-008; coding standards; setup guide; ARB approval record |
| S2 | Logging framework; masking; audit engine with hash chain; global error capture; error catalogue; log viewer tool | Logging and audit design note; masking test report; event ID catalogue |
| S3 | Configuration engine; DPAPI encryption; environment profiles; runtime reload; atmcfg CLI; last-known-good fallback | Configuration reference; key management procedure; JSON schema |
| S4 | SQLite data layer; five tables; repositories and Unit of Work; migrations bundle; retention and backup jobs; SQL Server provider | Data model diagram; data retention policy; migration runbook |
| S5 | Event bus; health checks; notifications; background jobs; resilience pipelines; watchdog; heartbeat client; central monitoring stub API | Services framework guide; OpenAPI spec; health check catalogue |
| S6 | Version 1.0 Foundation Release: signed installer, release build, E2E suite | Deployment Guide; Technical Documentation; API Documentation (DocFX and OpenAPI); test summary report; performance report; security review report and PCI control mapping; release notes |

## 13. Team Responsibilities and RACI

The four-person team covers every Scrum role by combining them; the bank provides a Product Owner and a Security Officer as named stakeholders. This plan assumes those two roles exist on the bank side; if not, the Solution Architect acts as proxy Product Owner.

| Role | Person | Primary responsibilities |
| --- | --- | --- |
| Solution Architect (also Scrum Master and Tech Lead) | SA | Architecture, ADRs, ARB liaison, code ownership of Core and Application, sprint ceremonies, risk register, release management, 50% hands-on development |
| Senior .NET Developer 1 | DEV1 | Logging and audit, persistence, security implementation (DPAPI, HMAC, masking), installer |
| Senior .NET Developer 2 | DEV2 | UI shell and MVVM, configuration engine, common services, monitoring stub API, CI/CD pipeline |
| QA Engineer | QA | Test strategy, automation framework, acceptance verification, performance and soak tests, coverage tracking, defect triage |
| Product Owner (bank) | PO | Backlog priority, story acceptance, scope decisions, Go/No-Go vote |
| Security Officer (bank) | SEC | Security requirements, threat model review, PCI mapping sign-off |

**RACI matrix** (R Responsible, A Accountable, C Consulted, I Informed)

| Activity | SA | DEV1 | DEV2 | QA | PO | SEC |
| --- | --- | --- | --- | --- | --- | --- |
| Architecture and ADRs | A, R | C | C | I | I | C |
| Solution structure and DI | A | R | R | I | I | I |
| CI/CD pipeline | A | C | R | C | I | I |
| Coding standards | A, R | C | C | C | I | I |
| Logging and audit framework | A | R | C | C | I | C |
| Configuration and encryption | A | C | R | C | I | C |
| Database layer | A | R | C | C | I | I |
| Common services framework | A | C | R | C | I | I |
| Test strategy and automation | C | C | C | A, R | I | I |
| Acceptance of stories | C | I | I | R | A | I |
| Security review and threat model | R | R | C | C | I | A |
| Performance validation | C | C | C | A, R | I | I |
| Installer and deployment guide | A | R | C | C | I | I |
| Technical and API documentation | A | R | R | C | I | I |
| Sprint ceremonies | A, R | C | C | C | C | I |
| Release Go/No-Go | R | C | C | C | A | C |

**Ceremonies and governance cadence**

| Ceremony | When | Duration | Attendees | Output |
| --- | --- | --- | --- | --- |
| Sprint planning | Day 1 of each sprint | 3 h | Team, PO | Sprint goal, committed backlog, task breakdown |
| Daily stand-up | Every working day | 15 min | Team | Blockers raised and owned |
| Backlog refinement | Weekly, Wednesday | 1.5 h | Team, PO | Next sprint stories meet Definition of Ready |
| Architecture sync | Weekly | 1 h | SA, DEVs | ADR updates, design decisions |
| Sprint review and demo | Last day of sprint | 1.5 h | Team, PO, stakeholders, SEC as needed | Increment accepted; gate signed |
| Retrospective | Last day of sprint | 1 h | Team | Two to three improvement actions |
| Leadership status report | Every two weeks after review | Written | SA to IT leadership | RAG status, burn-up, top risks, decisions needed |

## 14. Testing Strategy, QA Strategy and Automation

Quality is built in from Sprint 1: every story carries its own automated tests, and the QA Engineer owns the framework, the regression suite and the release evidence rather than testing at the end.

**Test pyramid and targets**

| Level | Scope | Tools | Target | Runs |
| --- | --- | --- | --- | --- |
| Unit | Core, Application, Infrastructure logic in isolation | xUnit, FluentAssertions, NSubstitute, AutoFixture | Above 80% line, 70% branch; about 70% of all tests | Every PR |
| Architecture | Layer dependency rules, naming, sealed types | NetArchTest.Rules | 100% of rules in Section 4 | Every PR |
| Integration | SQLite, file system, Event Log, DPAPI, host wiring, REST client against stub | xUnit, Testcontainers or LocalDB for SQL Server, WebApplicationFactory | All repositories and adapters; about 20% of tests | Every PR |
| Contract | Device ports against simulator; monitoring API OpenAPI; event serialisation | xUnit, Verify snapshot tests | All public contracts | Every PR |
| End-to-end | Startup to heartbeat scenarios with real host | xUnit with Generic Host; FlaUI for WPF UI smoke | 10 to 15 critical scenarios; about 10% of tests | Nightly and RC |
| Performance | NFR-01 to NFR-05 | BenchmarkDotNet, custom load harness, PerfView | Meet NFR targets | Nightly and Sprint 6 |
| Soak and resilience | 72-hour run, power-cut, disk full, corrupt config, DB corruption | Lab scripts, fault injection | Zero data loss, automatic recovery | Sprint 4 and Sprint 6 |
| Security | Masking, secret scan, SAST, dependency, config tampering | CodeQL, gitleaks, dotnet list package --vulnerable, custom tests | Zero Critical or High open | Every PR plus Sprint 6 review |

**QA process**

1. QA reviews acceptance criteria during refinement and writes test scenarios before development starts (three amigos with PO and a developer).
2. Developers write unit and integration tests within the story; QA adds E2E, negative and fault-injection tests.
3. QA verifies each story on the CI build, logs defects with severity, and accepts against the criteria.
4. Regression suite runs nightly; any failure is triaged next morning and fixed before new work.
5. Sprint 6 produces the test summary report: coverage, pass rate, defects by severity, NFR results and residual risk.

**Defect severity and exit rules**

| Severity | Definition | Fix expectation | Allowed at release |
| --- | --- | --- | --- |
| Critical | Data loss, security breach, crash at startup, sensitive data exposure | Fix immediately; blocks merge | 0 |
| High | Core capability broken with no workaround | Fix within the sprint | 0 |
| Medium | Capability impaired, workaround exists | Fix within next sprint | 5 or fewer, each with PO acceptance |
| Low | Cosmetic or minor | Backlog | No limit, documented |

**Automation strategy**

- Everything that can run headless runs in GitHub Actions on windows-latest runners; UI smoke tests (FlaUI) run nightly on a self-hosted Windows runner with an interactive session.
- Test data is generated by builders and AutoFixture; no production data is ever used, and test card numbers come from published test ranges only.
- The device simulator is scriptable (JSON scenarios) so Phase 2 can replay vendor fault codes without hardware.
- Coverage is collected with coverlet and published with ReportGenerator as a PR comment and artifact; the gate is 80% on new code from Sprint 1 and on the whole solution from Sprint 6.
- Flaky tests are quarantined only with a tracked defect and fixed within the sprint; they never silently stay disabled.
- Automation code follows the same coding standards and review rules as production code.

## 15. CI/CD Strategy, Branching and Workflow

GitHub Actions builds, tests, scans and packages every change; only a green, reviewed PR reaches main, and only a tagged, signed build from main reaches a terminal. Azure DevOps Pipelines can host the same stages one-for-one if the bank standardises on it.

**Git branching strategy (trunk-based with short-lived branches)**

| Branch | Purpose | Created from | Merges into | Rules |
| --- | --- | --- | --- | --- |
| main | Always releasable trunk | n/a | n/a | Protected; PR only; required checks; 1 review plus code owner; squash merge; signed commits |
| feature/US-xxx-short-name | One user story | main | main via PR | Lives 3 days or less; rebased on main daily |
| bugfix/BUG-xxx-short-name | Defect fix | main | main via PR | Same rules as feature |
| release/1.0 | Stabilisation for a release | main at RC cut (week 11) | Tagged v1.0.0; fixes cherry-picked back to main | Only fixes approved by SA and PO |
| hotfix/1.0.x | Urgent production fix | Tag v1.0.0 | release/1.0 and main | Emergency change process; security review if security-related |

Commits follow Conventional Commits (feat, fix, refactor, test, docs, chore, with the story ID) so release notes are generated automatically. Versions follow SemVer from Git tags (v1.0.0-rc.1, v1.0.0).

**Pipeline workflow**

```text
  PR opened / updated                      Merge to main                      Tag v*.*.*
  -------------------                      -------------                      ----------
  [ci.yml]                                 [ci.yml + package.yml]             [release.yml]
  1. Checkout + cache NuGet                1-6 as PR                          1. Build Release config
  2. dotnet format --verify-no-changes     7. Integration tests (SQLite,      2. Full test + E2E suite
  3. Build (warnings as errors)               LocalDB SQL Server)             3. Sign binaries (code signing)
  4. Unit + architecture tests             8. Coverage gate 80% (new code)    4. Build WiX MSI, sign MSI
  5. Coverage report (PR comment)          9. Publish build artifact          5. SBOM (CycloneDX)
  6. Security: CodeQL, gitleaks,          10. Deploy to DEV lab VM            6. Generate release notes + DocFX
     dependency + licence check                                               7. GitHub Release (draft)
                                                                              8. Manual approval -> SIT/UAT lab
  Nightly: E2E + FlaUI smoke on self-hosted runner, perf benchmarks, soak (weekly)
```

**Reference workflow (ci.yml excerpt)**

```yaml
name: ci
on:
  pull_request:
    branches: [ main ]
  push:
    branches: [ main ]
permissions:
  contents: read
  security-events: write
jobs:
  build-test:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0 }
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - run: dotnet restore --locked-mode
      - run: dotnet format --verify-no-changes
      - run: dotnet build -c Release --no-restore -warnaserror
      - run: dotnet test -c Release --no-build --collect:"XPlat Code Coverage" --settings tests/coverlet.runsettings
      - uses: danielpalme/ReportGenerator-GitHub-Action@5
        with: { reports: '**/coverage.cobertura.xml', targettype: 'Cobertura;MarkdownSummaryGithub' }
  security:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: github/codeql-action/init@v3
        with: { languages: csharp }
      - run: dotnet build -c Release
      - uses: github/codeql-action/analyze@v3
      - uses: gitleaks/gitleaks-action@v2
      - run: dotnet list package --vulnerable --include-transitive
```

**Environments and promotion**

| Environment | Purpose | Deployed by | Approval |
| --- | --- | --- | --- |
| DEV lab VM | Continuous integration verification | Automatic on merge to main | None |
| SIT lab | System integration tests with monitoring stub | Release workflow | SA |
| UAT lab ATM | Acceptance on reference ATM image | Release workflow | PO and QA |
| Pilot (Phase 2) | Limited live terminals | Change management | CAB, PO, SEC |

Pipeline secrets (signing certificate, deploy credentials) live in GitHub environment secrets with required reviewers; runners never store them on disk. Self-hosted runners are hardened, isolated from the bank production network and rebuilt monthly.

## 16. Security Requirements and Baseline Controls

Phase 1 handles no live card data, but it builds the controls every later phase depends on, mapped to PCI DSS v4.0 and secure software practice so the Security Officer can sign them off in Sprint 6. Requirement references are indicative and are confirmed against the bank's QSA interpretation during that review.

| ID | Control | Implementation in Phase 1 | PCI DSS v4.0 reference | Verified by |
| --- | --- | --- | --- | --- |
| SEC-01 | No storage of sensitive authentication data | PIN, full track, CVV never logged or stored; PAN masked (first 6, last 4 at most) | Req 3.3, 3.4 | Masking test suite; DB and log scan |
| SEC-02 | Protect stored account data | Column encryption via IKeyProvider; tokenised or truncated PAN only | Req 3.5 | Code review; DB scan |
| SEC-03 | Cryptographic key management | DPAPI machine-scope keys, key versioning, documented rotation; HSM/TPM path in Phase 2 | Req 3.6, 3.7 | Key procedure review |
| SEC-04 | Strong cryptography in transit | TLS 1.2 or higher only for REST; certificate validation enforced; mTLS planned for Phase 2 | Req 4.2 | Integration test against stub with weak TLS rejected |
| SEC-05 | Secure development lifecycle | Threat model, secure coding standards, peer review, security in DoD | Req 6.2 | Sprint 6 security review |
| SEC-06 | Vulnerability management of components | Dependabot, vulnerable package check, SBOM per release | Req 6.3 | CI evidence |
| SEC-07 | Static analysis | CodeQL on every PR; zero Critical or High at release | Req 6.2.4 | CI evidence |
| SEC-08 | Secrets management | No secrets in source; gitleaks and push protection; encrypted config | Req 8.6 | CI evidence; config audit |
| SEC-09 | Audit logging | Tamper-evident audit of security, config, operator and system events with UTC time | Req 10.2, 10.3 | Hash chain verifier test |
| SEC-10 | Time synchronisation readiness | All timestamps UTC; NTP status in health checks | Req 10.6 | Health check test |
| SEC-11 | Least privilege | Client runs as a dedicated non-admin service account; installer sets file ACLs on data, logs, keys | Req 7.2 | Install test; ACL check |
| SEC-12 | Software integrity | Code-signed binaries and MSI; config integrity check; only signed updates accepted (Phase 2 agent) | Req 6.4, 11.5 | Signature verification test |
| SEC-13 | Error handling without leakage | User-facing errors show codes only; stack traces only in protected logs | Req 6.2.4 | Code review; UI review |
| SEC-14 | Input validation | All configuration and API inputs validated; parameterised queries via EF Core only | Req 6.2.4 | Tests; SAST |
| SEC-15 | Hardened platform baseline | Documented OS baseline for Windows IoT LTSC (kiosk lockdown, USB control, firewall rules) as input to Phase 2 | Req 2.2 | Deployment guide review |

**Security activities per sprint**

- Sprint 1: threat model kickoff (STRIDE) of the architecture; security tooling in CI.
- Sprint 2: Security Officer review of masking and audit design.
- Sprint 3: key management procedure review.
- Sprint 4: data protection review of the schema and retention.
- Sprint 5: review of REST interface and resilience behaviour under attack scenarios (replay, flood).
- Sprint 6: full security review, threat model update, PCI control mapping sign-off, residual risk acceptance.

## 17. Coding Standards

Standards are enforced by tooling first and review second: .editorconfig, Roslyn analyzers, StyleCop and architecture tests fail the build, so reviewers spend their time on design and security.

**Language and project settings**

- C# 12 on .NET 8; Nullable enable; ImplicitUsings enable; TreatWarningsAsErrors true; AnalysisLevel latest-recommended.
- Central package management (Directory.Packages.props) and lock files; no floating versions.
- File-scoped namespaces matching folder paths; one public type per file.

**Naming**

| Element | Convention | Example |
| --- | --- | --- |
| Namespaces, classes, records, methods, properties | PascalCase | ATM.Application.Audit, AuditService.RecordAsync |
| Interfaces | I prefix | IAuditService |
| Private fields | \_camelCase | \_auditRepository |
| Local variables, parameters | camelCase | terminalId |
| Async methods | Async suffix | SaveChangesAsync |
| Constants | PascalCase | MaxRetryCount |
| ViewModels and Views | Suffix ViewModel and View | HealthViewModel, HealthView |
| Options classes | Suffix Options with SectionName constant | LoggingOptions.SectionName |
| Tests | Method\_State\_ExpectedResult | RecordAsync\_WhenChainBroken\_RaisesTamperEvent |

**Design rules**

1. Respect layer boundaries in Section 4; architecture tests are never disabled.
2. Constructor injection only; no service locator, no static mutable state, no new of services outside the composition root.
3. Classes are sealed by default; prefer records and immutable value objects in Core.
4. Async all the way: no .Result, .Wait() or async void (except WPF event handlers); pass CancellationToken through every async API.
5. Use structured logging message templates, never string interpolation in log calls; never log objects that may hold card or key data without a destructuring policy.
6. Exceptions: throw domain-specific exceptions with ErrorCode; never swallow exceptions; catch only where you can handle or add context.
7. Guard clauses with ArgumentNullException.ThrowIfNull and similar; validate at boundaries.
8. Use TimeProvider for time and IFileSystem abstractions where tests need control; all times are UTC.
9. MVVM: no logic in code-behind; ViewModels never reference WPF controls; commands via RelayCommand or AsyncRelayCommand.
10. Methods stay short (guideline 30 lines, cyclomatic complexity 10 or less); classes have one reason to change.
11. Public APIs carry XML documentation comments; comments explain why, not what.
12. Security-sensitive code (crypto, masking, audit, config protection) needs a second reviewer from the architect.

**Code review checklist**

- [ ] Story ID in PR title; description explains why
- [ ] Tests cover happy path, edge cases and failure paths
- [ ] No sensitive data in logs, exceptions, test data or config
- [ ] Dependencies flow inward; no new package without architect approval
- [ ] Cancellation, disposal and thread safety considered
- [ ] Configuration keys documented; audit events added for security-relevant actions

## 18. Development Environment Setup

A new engineer builds the solution and runs all tests within one hour using bootstrap.ps1 and the versions below; global.json pins the SDK so every machine and runner build identically.

| Component | Version or setting | Notes |
| --- | --- | --- |
| OS | Windows 11 Pro or Enterprise 23H2 or later (dev); Windows 10/11 IoT Enterprise LTSC (target) | Hyper-V enabled for lab VM |
| .NET SDK | 8.0.x latest patch, pinned in global.json | rollForward latestPatch |
| IDE | Visual Studio 2022 17.8 or later with .NET desktop workload, or JetBrains Rider | ReSharper or Rider inspections optional, .editorconfig is the authority |
| Git | 2.40 or later; commit signing with GPG or SSH key | Git LFS not required |
| Database tools | DB Browser for SQLite; SQL Server 2022 Developer or LocalDB | LocalDB used in CI for SQL Server tests |
| EF Core tooling | dotnet-ef 8.x as a local tool (.config/dotnet-tools.json) | Restored by dotnet tool restore |
| Other tools | ReportGenerator, DocFX, WiX Toolset v4, gitleaks | Installed by bootstrap.ps1 as local or global tools |
| Test hardware | One reference ATM PC (or industrial PC with same spec) in the lab from Sprint 4 | Requested at kickoff (R-07) |

**Setup steps**

1. Clone the repository and run `./build/bootstrap.ps1` (checks SDK, restores local tools, installs pre-commit hooks for format and gitleaks).
2. Run `dotnet build ATM.sln` then `dotnet test`; all tests must pass on a clean clone.
3. Set `ATM_ENVIRONMENT=Development`; development secrets go in user-secrets, never in appsettings files.
4. Run `atmcfg encrypt` to create local encrypted test values where a story needs them.
5. Start `ATM.Host` (WPF client) and `ATM.Monitoring.Api` together with the provided launch profile; the device simulator loads `simulator/default.json`.
6. Read `docs/architecture` and `CONTRIBUTING.md` before the first PR.

## 19. Repository and Folder Structure

One mono-repository holds the client, the monitoring stub, tests, build scripts and documentation, so a single commit and tag version the whole foundation. ATM.Host is the WPF executable and composition root; ATM.UI is a WPF class library of Views and ViewModels.

```text
MyATMGuy/
+-- .github/
|   +-- workflows/                 ci.yml, nightly.yml, release.yml, codeql.yml
|   +-- CODEOWNERS
|   +-- pull_request_template.md
|   +-- dependabot.yml
+-- build/
|   +-- bootstrap.ps1              dev machine setup
|   +-- installer/                 WiX v4 project, signing scripts
+-- docs/
|   +-- architecture/              architecture document, diagrams
|   +-- adr/                       ADR-001 ... ADR-0nn
|   +-- guides/                    deployment guide, runbook, configuration reference
|   +-- api/                       DocFX project
+-- src/
|   +-- ATM.Core/
|   |   +-- Entities/              Transaction, Device, AuditEntry, ErrorRecord, ConfigItem
|   |   +-- ValueObjects/          TerminalId, MaskedPan, Money, CorrelationId
|   |   +-- Events/                domain events
|   |   +-- Errors/                ErrorCode catalogue, domain exceptions
|   +-- ATM.Application/
|   |   +-- Abstractions/          IAuditService, IEventBus, IUnitOfWork, IKeyProvider, ...
|   |   +-- Devices/               IDevicePort, ICardReaderPort, ICashDispenserPort, ...
|   |   +-- Audit/  Configuration/  Health/  Notifications/  Jobs/
|   |   +-- DependencyInjection.cs AddApplication()
|   +-- ATM.Infrastructure/
|   |   +-- Persistence/           AtmDbContext, Configurations/, Repositories/, Migrations/
|   |   +-- Logging/               Serilog setup, MaskingEnricher, sinks
|   |   +-- Security/              DpapiKeyProvider, HmacChain, ConfigIntegrity
|   |   +-- Messaging/             ChannelEventBus, DeadLetterStore
|   |   +-- Resilience/            Polly pipelines
|   |   +-- Monitoring/            MonitoringApiClient, HeartbeatJob
|   |   +-- Devices/Simulator/     simulator adapters (Phase 2: Devices/Xfs/<Vendor>/)
|   |   +-- DependencyInjection.cs AddInfrastructure()
|   +-- ATM.UI/
|   |   +-- Views/  ViewModels/  Navigation/  Themes/  Converters/
|   +-- ATM.Host/
|   |   +-- App.xaml(.cs)          Generic Host bootstrap, global exception handlers
|   |   +-- appsettings*.json      layered configuration
|   +-- ATM.Monitoring.Api/        ASP.NET Core minimal API stub (SQL Server)
|   +-- ATM.Tools.Config/          atmcfg CLI
|   +-- ATM.Tools.LogViewer/       log search utility
+-- tests/
|   +-- ATM.Core.Tests/
|   +-- ATM.Application.Tests/
|   +-- ATM.Infrastructure.Tests/  (integration: SQLite, DPAPI, EventLog)
|   +-- ATM.Architecture.Tests/
|   +-- ATM.Monitoring.Api.Tests/
|   +-- ATM.EndToEnd.Tests/        incl. FlaUI smoke
|   +-- ATM.Performance.Tests/     BenchmarkDotNet
|   +-- coverlet.runsettings
+-- simulator/                     device simulator scenarios (JSON)
+-- .editorconfig
+-- Directory.Build.props
+-- Directory.Packages.props
+-- global.json
+-- ATM.sln
+-- CONTRIBUTING.md
+-- README.md
```

The ATM.Tests deliverable named in Sprint 1 is realised as the tests folder, one test project per production project, which keeps test dependencies aligned with layer boundaries.

## 20. Release Readiness Checklist

Version 1.0 Foundation Release ships only when every item below is checked and the Go/No-Go meeting in week 12 records approval from the Product Owner, Solution Architect and Security Officer.

**Quality**

- [ ] Unit test coverage above 80% line and 70% branch (report attached)
- [ ] All unit, integration, contract and E2E suites green on the release candidate
- [ ] Zero open Critical or High defects; Medium defects 5 or fewer, each accepted by PO
- [ ] NFR-01 to NFR-18 results documented; performance and 72-hour soak reports attached
- [ ] Power-loss and corruption recovery tests passed on the reference ATM PC

**Security and compliance**

- [ ] CodeQL, dependency and secret scans clean (no Critical or High open)
- [ ] Threat model updated and reviewed
- [ ] Masking and DB scan tests prove no sensitive data in logs or storage
- [ ] PCI DSS control mapping signed off by Security Officer
- [ ] SBOM generated and archived with the release
- [ ] Binaries and MSI signed with the bank code-signing certificate; signatures verified

**Deployment**

- [ ] Silent install, upgrade from RC and rollback tested on Windows IoT Enterprise LTSC image
- [ ] Database migration from empty and from RC tested with backup and restore
- [ ] Service account, file ACLs and Event Log source created by installer
- [ ] Environment configuration files prepared and encrypted for SIT and UAT
- [ ] Monitoring stub receives heartbeats from the lab terminal

**Documentation**

- [ ] Deployment Guide validated by someone outside the team
- [ ] Technical Documentation and ADRs current
- [ ] API Documentation (DocFX and OpenAPI) published
- [ ] Operations runbook (logs, health, recovery, key rotation) complete
- [ ] Release notes generated and reviewed

**Governance**

- [ ] Release tagged v1.0.0 from release/1.0; artifacts immutable in GitHub Releases
- [ ] Change request raised with CAB for lab and UAT installation
- [ ] Known issues and residual risks accepted in writing
- [ ] Phase 2 backlog seeded with deferred and Should items
- [ ] Go/No-Go decision recorded with approvers and date

## Week-by-Week Execution Plan

Each week has a named focus, owners and an exit check, so leadership can see slippage within days rather than at the sprint gate.

| Week | Sprint | Focus | Key activities and owners | End-of-week exit check |
| --- | --- | --- | --- | --- |
| 1 | S1 | Kickoff and scaffolding | Kickoff with PO and SEC; request lab PC, image and signing cert (SA). Create repo, solution, projects, Directory props (DEV1). GitHub Actions build-test (DEV2). Test stack and conventions (QA). Draft ADR-001 to 004 (SA) | Solution builds in CI; ADR drafts shared with ARB |
| 2 | S1 | DI, shell, standards | Generic Host composition root (DEV1). MVVM shell and navigation (DEV2). Analyzers, .editorconfig, branch protection, CODEOWNERS (SA). Device ports and simulator (SA, DEV1). Architecture tests (QA) | Sprint 1 demo; ARB approval; CI enforces standards |
| 3 | S2 | Logging core | Serilog, enrichers, file and Event Log sinks (DEV1). Correlation IDs, level switching (DEV2). Masking test patterns (QA). Velocity check (SA) | Structured logs visible with correlation; masking tests written |
| 4 | S2 | Audit and errors | Audit service and HMAC chain (DEV1). Global exception handling, error catalogue (DEV2). Log viewer tool (DEV2). SEC review of masking and audit (SA) | Sprint 2 demo: audit recorded, errors captured, logs searchable; velocity re-baselined |
| 5 | S3 | Configuration core | Layered config and profiles (DEV2). Options validation and ValidateOnStart (DEV2). DPAPI provider and IKeyProvider (DEV1). Secret scanning in CI (QA) | Invalid config blocks startup in tests; secrets encrypted |
| 6 | S3 | Runtime configuration | IOptionsMonitor reload and last-known-good (DEV2). Integrity check, atmcfg CLI (DEV1). Config audit (DEV1). JSON schema (SA). Key procedure review with SEC (SA) | Sprint 3 demo: dynamic load, validation, encryption |
| 7 | S4 | Persistence core | EF Core and SQLite with WAL (DEV1). Entities and mappings (DEV1, SA). Repositories and Unit of Work (DEV2). Repository integration tests (QA) | CRUD on all five tables passing in CI |
| 8 | S4 | Migrations and data lifecycle | Migrations with backup and restore (DEV1). Column encryption, retention, integrity check (DEV2). SQL Server provider (SA). Power-cut tests on lab PC (QA) | Sprint 4 demo: migrations, local transaction storage; power-cut results |
| 9 | S5 | Event bus and health | Channel event bus, contracts, dead-letter (DEV2). Health checks (DEV1). Service module conventions (SA). Event bus load test (QA) | Pub/sub and health checks working in E2E test |
| 10 | S5 | Services and monitoring | Notifications, background jobs, Polly pipelines (DEV1). Heartbeat client and monitoring stub API (DEV2). Watchdog (SA). Maintenance health view (DEV2). E2E suite skeleton (QA) | Sprint 5 demo: heartbeats arrive at stub; services resolved from DI |
| 11 | S6 | Hardening and RC | Coverage gap closure (all). E2E suite complete (QA). Performance runs and 72-hour soak start (QA). Threat model update and CodeQL triage (SA, DEV1). WiX installer and signing (DEV1). Cut release/1.0 and RC1 (SA) | RC1 installed on lab ATM; soak running; coverage above 80% |
| 12 | S6 | Release | Defect fixing on release branch (DEV1, DEV2). Deployment guide, technical and API docs finalised (DEV2, SA). Soak and performance reports (QA). Security review sign-off (SEC). Go/No-Go meeting (PO). Tag v1.0.0 | Release checklist complete; Version 1.0 Foundation Release approved |

**Milestones and gates**

| Milestone | Week | Approver |
| --- | --- | --- |
| M1 Architecture approved, CI operational | 2 | ARB, SA |
| M2 Logging and audit framework accepted | 4 | PO, SEC |
| M3 Configuration framework accepted | 6 | PO, SEC |
| M4 Database layer accepted | 8 | PO |
| M5 Common services accepted | 10 | PO |
| M6 Release candidate on lab ATM | 11 | SA, QA |
| M7 Version 1.0 Foundation Release Go/No-Go | 12 | PO, SA, SEC |

**Next step:** confirm the kickoff date, the bank-side Product Owner and Security Officer, and the lab hardware request, so that Week 1 can start against this plan.
