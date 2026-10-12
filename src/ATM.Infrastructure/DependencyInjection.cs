using ATM.Application.Audit;
using ATM.Application.Devices;
using ATM.Infrastructure.Audit;
using ATM.Infrastructure.Devices.Simulator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ATM.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers every Infrastructure adapter. In Phase 1 the only device adapters are the simulators.</summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        Action<SimulatorOptions>? configureSimulator = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSimulatedDevices(configureSimulator);
        services.TryAddSingleton(new AuditFileOptions());
        services.TryAddSingleton<IAuditStore, JsonLinesAuditStore>();
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
