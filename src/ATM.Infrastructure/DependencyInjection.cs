using ATM.Application.Audit;
using ATM.Application.Devices;
using ATM.Application.Terminal;
using ATM.Infrastructure.Audit;
using ATM.Infrastructure.Logging;
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
        services.TryAddSingleton(sp => AuditKeyLoader.LoadOrCreate(sp.GetRequiredService<AuditFileOptions>().KeyPath));

        return services.AddAtmLogging(configureLogging);
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
