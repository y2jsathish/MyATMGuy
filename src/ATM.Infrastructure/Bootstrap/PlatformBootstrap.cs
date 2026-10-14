using ATM.Application;
using ATM.Application.Configuration;
using ATM.Application.Modules;
using ATM.Infrastructure.Configuration;
using ATM.Infrastructure.Devices.Simulator;
using ATM.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ATM.Infrastructure.Bootstrap;

public sealed record PlatformStart(
    string BasePath,
    AtmEnvironment Environment,
    IKeyProvider Keys,
    IConfigurationRoot Raw,
    BootstrapResult Configuration)
{
    /// <summary>Decrypted configuration to hand to the host builder.</summary>
    public IConfigurationRoot Decrypted => Configuration.Configuration.WithDecryption(new SecretProtector(Keys));
}

/// <summary>
/// The startup sequence shared by the real application and the integration tests, so tests exercise the same composition:
/// load and validate configuration (with last-known-good fallback), then register every platform layer.
/// </summary>
public static class PlatformBootstrap
{
    public static PlatformStart Prepare(string basePath, AtmEnvironment environment, string? keysDirectory = null)
    {
        var keys = KeyProviderFactory.Create(keysDirectory ?? Path.Combine(basePath, "keys"), environment);
        var raw = AtmConfiguration.BuildRaw(basePath, environment);
        var bootstrap = ConfigBootstrap.Load(basePath, environment, new SecretProtector(keys));
        return new PlatformStart(basePath, environment, keys, raw, bootstrap);
    }

    /// <summary>Registers configuration, application services, infrastructure (simulators), persistence and the platform module.</summary>
    public static IServiceCollection AddPlatform(this IServiceCollection services, PlatformStart start, IConfiguration configuration, Action<SimulatorOptions>? configureSimulator = null)
    {
        return services
            .AddAtmConfiguration(start.Raw, start.Configuration, start.Environment, start.Keys, start.BasePath)
            .AddApplication()
            .AddInfrastructure(
                configureSimulator,
                configureLogging: o => configuration.GetSection(Logging.LoggingOptions.SectionName).Bind(o),
                configureAudit: a => a.KeyDirectory = Path.Combine(start.BasePath, "keys"))
            .AddAtmPersistence(o => configuration.GetSection(DatabaseOptions.SectionName).Bind(o))
            .AddModules(configuration, new PlatformServicesModule())
            .AddTransient<ATM.Application.Transactions.CashWithdrawalFlow>()
            .AddTransient<ATM.Application.Transactions.CardServiceRunner>()
            .AddTransient<ATM.Application.Transactions.BalanceInquiryFlow>()
            .AddTransient<ATM.Application.Transactions.MiniStatementFlow>()
            .AddTransient<ATM.Application.Transactions.PinChangeFlow>();
    }
}
