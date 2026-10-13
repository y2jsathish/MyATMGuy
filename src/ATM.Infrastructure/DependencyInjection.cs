using System.Security.Cryptography;
using ATM.Application.Audit;
using ATM.Application.Configuration;
using ATM.Application.Errors;
using ATM.Application.Persistence;
using ATM.Application.Devices;
using ATM.Application.Terminal;
using ATM.Infrastructure.Audit;
using ATM.Infrastructure.Configuration;
using ATM.Infrastructure.Logging;
using ATM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using ATM.Infrastructure.Devices.Simulator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ATM.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers every Infrastructure adapter. In Phase 1 the only device adapters are the simulators.</summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        Action<SimulatorOptions>? configureSimulator = null,
        Action<LoggingOptions>? configureLogging = null,
        Action<AuditFileOptions>? configureAudit = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSimulatedDevices(configureSimulator);

        var audit = new AuditFileOptions();
        configureAudit?.Invoke(audit);
        services.TryAddSingleton(audit);
        services.TryAddSingleton<IAuditStore, JsonLinesAuditStore>();
        services.TryAddSingleton<IKeyProvider>(sp => KeyProviderFactory.Create(
            sp.GetRequiredService<AuditFileOptions>().KeyDirectory,
            AtmEnvironment.Dev));
        services.TryAddSingleton<SecretProtector>();
        services.TryAddSingleton(sp => AuditKeyDerivation.Derive(sp.GetRequiredService<IKeyProvider>()));

        return services.AddAtmLogging(configureLogging);
    }

    /// <summary>
    /// Registers layered, validated, decrypted configuration with last-known-good and runtime reload.
    /// Call before AddApplication and AddInfrastructure so these registrations take precedence.
    /// </summary>
    public static IServiceCollection AddAtmConfiguration(
        this IServiceCollection services,
        IConfigurationRoot raw,
        BootstrapResult bootstrap,
        AtmEnvironment environment,
        IKeyProvider keys,
        string basePath)
    {
        var protector = new SecretProtector(keys);
        var decrypted = bootstrap.Configuration.WithDecryption(protector);

        var terminal = decrypted.GetSection(TerminalOptions.SectionName).Get<TerminalOptions>() ?? new TerminalOptions();
        var logging = decrypted.GetSection(LoggingOptions.SectionName).Get<LoggingOptions>() ?? new LoggingOptions();

        services.AddSingleton(keys);
        services.AddSingleton(new AtmEnvironmentHolder(environment));
        services.AddSingleton(protector);
        services.AddSingleton<IConfiguration>(decrypted);
        services.AddSingleton(terminal);
        services.AddSingleton(logging);
        services.AddSingleton(new TerminalInfo(terminal.TerminalId, TerminalInfo.FromEnvironment().Version, Environment.MachineName));
        services.AddSingleton(sp =>
        {
            var governor = new ConfigGovernor(
                flat => AtmConfiguration.Validate(flat, environment),
                sp.GetRequiredService<IAuditService>(),
                sp.GetRequiredService<IErrorCounters>(),
                snapshot => ConfigBootstrap.SaveLastKnownGood(basePath, snapshot));
            governor.Initialize(bootstrap.Raw);
            return governor;
        });
        services.AddSingleton<LiveSettings>();
        services.AddSingleton(sp => new ConfigReloadService(
            raw,
            sp.GetRequiredService<ConfigGovernor>(),
            sp.GetRequiredService<LiveSettings>(),
            sp.GetRequiredService<LogLevelController>(),
            sp.GetRequiredService<ILogger<ConfigReloadService>>(),
            sp.GetService<ATM.Application.Events.IEventBus>()));
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ConfigReloadService>());
        return services;
    }

    /// <summary>
    /// SQLite persistence with WAL, repositories, unit of work, migrations and retention. Audit records move to the database,
    /// falling back to the audit file if a write fails. Call after AddInfrastructure.
    /// </summary>
    public static IServiceCollection AddAtmPersistence(this IServiceCollection services, Action<DatabaseOptions>? configure = null, RetentionOptions? retention = null)
    {
        var options = new DatabaseOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton(retention ?? new RetentionOptions());
        services.TryAddSingleton<SqlitePragmaInterceptor>();
        services.AddDbContextFactory<AtmDbContext>((sp, builder) =>
            builder.UseSqlite(sp.GetRequiredService<DatabaseOptions>().ConnectionString).AddInterceptors(sp.GetRequiredService<SqlitePragmaInterceptor>()));
        services.TryAddSingleton<IUnitOfWorkFactory, UnitOfWorkFactory>();
        services.TryAddSingleton<DatabaseMaintenance>();

        services.RemoveAll<IAuditStore>();
        services.AddSingleton<IAuditStore>(sp => new FallbackAuditStore(
            new EfAuditStore(sp.GetRequiredService<IDbContextFactory<AtmDbContext>>()),
            new JsonLinesAuditStore(sp.GetRequiredService<AuditFileOptions>()),
            sp.GetRequiredService<IErrorCounters>()));
        return services;
    }

    /// <summary>Serilog behind Microsoft.Extensions.Logging, with masking, enrichment, rolling files and runtime level control.</summary>
    public static IServiceCollection AddAtmLogging(this IServiceCollection services, Action<LoggingOptions>? configure = null)
    {
        var options = new LoggingOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<LogLevelController>();
        services.TryAddSingleton<UnhandledExceptionHooks>();
        services.AddLogging(builder => builder.Services.AddSingleton<ILoggerProvider>(sp =>
        {
            var logger = AtmLogging.Create(
                sp.GetRequiredService<LoggingOptions>(),
                sp.GetRequiredService<TerminalInfo>(),
                sp.GetRequiredService<LogLevelController>(),
                sinks =>
                {
                    if (options.WindowsEventLog && OperatingSystem.IsWindows())
                    {
                        EventLogSupport.TryAddEventLog(sinks, options);
                    }
                });
            return new Serilog.Extensions.Logging.SerilogLoggerProvider(logger, dispose: true);
        }));
        return services;
    }

    public static IServiceCollection AddSimulatedDevices(
        this IServiceCollection services,
        Action<SimulatorOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new SimulatorOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        services.TryAddSingleton<SimulatedCardReader>();
        services.TryAddSingleton<SimulatedPinPad>();
        services.TryAddSingleton<SimulatedCashDispenser>();
        services.TryAddSingleton<SimulatedReceiptPrinter>();

        services.TryAddSingleton<ICardReaderPort>(sp => sp.GetRequiredService<SimulatedCardReader>());
        services.TryAddSingleton<IPinPadPort>(sp => sp.GetRequiredService<SimulatedPinPad>());
        services.TryAddSingleton<ICashDispenserPort>(sp => sp.GetRequiredService<SimulatedCashDispenser>());
        services.TryAddSingleton<IReceiptPrinterPort>(sp => sp.GetRequiredService<SimulatedReceiptPrinter>());

        services.AddSingleton<IDevicePort>(sp => sp.GetRequiredService<ICardReaderPort>());
        services.AddSingleton<IDevicePort>(sp => sp.GetRequiredService<IPinPadPort>());
        services.AddSingleton<IDevicePort>(sp => sp.GetRequiredService<ICashDispenserPort>());
        services.AddSingleton<IDevicePort>(sp => sp.GetRequiredService<IReceiptPrinterPort>());
        return services;
    }
}
