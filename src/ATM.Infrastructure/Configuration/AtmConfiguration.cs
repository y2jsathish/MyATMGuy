using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using ATM.Application.Configuration;
using ATM.Core.Errors;
using ATM.Infrastructure.Logging;
using ATM.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace ATM.Infrastructure.Configuration;

public static class AtmConfiguration
{
    public const string TerminalFile = "terminal.json";
    public const string LastKnownGoodFile = "config.lkg.json";

    /// <summary>Layers, lowest to highest precedence: appsettings.json, appsettings.{Env}.json, terminal.json, ATM_ environment variables.</summary>
    public static IConfigurationRoot BuildRaw(string basePath, AtmEnvironment environment, bool reloadOnChange = true) =>
        new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange)
            .AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange)
            .AddJsonFile(TerminalFile, optional: true, reloadOnChange)
            .AddEnvironmentVariables("ATM_")
            .Build();

    public static Dictionary<string, string?> Flatten(IConfiguration configuration) =>
        configuration.AsEnumerable().Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>Validates typed options and secret rules. Returns one message per problem, each naming the offending key.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string?> flat, AtmEnvironment environment)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(flat).Build();
        var errors = new List<string>();

        Check<TerminalOptions>(configuration, TerminalOptions.SectionName, errors);
        Check<LoggingOptions>(configuration, LoggingOptions.SectionName, errors);
        Check<DatabaseOptions>(configuration, DatabaseOptions.SectionName, errors);

        if (environment == AtmEnvironment.Prod)
        {
            errors.AddRange(SecretKeys.FindPlaintext(flat).Select(k => $"{k}: plaintext secret is not allowed in Prod; encrypt it with atmcfg."));
            if (configuration.GetSection(TerminalOptions.SectionName).GetValue<bool>(nameof(TerminalOptions.DebugFeatures)))
            {
                errors.Add($"{TerminalOptions.SectionName}:{nameof(TerminalOptions.DebugFeatures)}: debug features are forbidden in Prod.");
            }
        }

        return errors;
    }

    private static void Check<T>(IConfiguration configuration, string section, List<string> errors)
        where T : class, new()
    {
        var instance = configuration.GetSection(section).Get<T>() ?? new T();
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true))
        {
            return;
        }

        foreach (var result in results)
        {
            var keys = result.MemberNames.Any() ? result.MemberNames.Select(m => $"{section}:{m}") : [section];
            errors.Add($"{string.Join(", ", keys)}: {result.ErrorMessage}");
        }
    }
}

public sealed record BootstrapResult(
    IConfigurationRoot Configuration,
    IReadOnlyDictionary<string, string?> Raw,
    IReadOnlyList<string> Errors,
    bool UsedLastKnownGood)
{
    public bool IsValid => Errors.Count == 0;
}

public static class ConfigBootstrap
{
    /// <summary>
    /// Loads and validates configuration for startup. When the files are invalid or tampered with, falls back to the last known good
    /// snapshot if one exists; Errors then still lists what was wrong so the caller can audit it.
    /// </summary>
    public static BootstrapResult Load(string basePath, AtmEnvironment environment, SecretProtector? protector, bool requireManifest = false)
    {
        var raw = AtmConfiguration.BuildRaw(basePath, environment);
        var flat = AtmConfiguration.Flatten(raw);
        var errors = new List<string>(AtmConfiguration.Validate(flat, environment));

        var manifestPath = Path.Combine(basePath, ConfigIntegrity.ManifestName);
        if (File.Exists(manifestPath) || requireManifest || environment == AtmEnvironment.Prod)
        {
            var integrity = ConfigIntegrity.Verify(basePath);
            errors.AddRange(integrity.Problems.Select(p => $"{ErrorCatalogue.ConfigurationTampered.Code}: {p}"));
        }

        var lkgPath = Path.Combine(basePath, AtmConfiguration.LastKnownGoodFile);
        if (errors.Count > 0 && File.Exists(lkgPath))
        {
            var lkg = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(lkgPath));
            if (lkg is not null && AtmConfiguration.Validate(lkg, environment).Count == 0)
            {
                var fallback = new ConfigurationBuilder().AddInMemoryCollection(lkg).Build();
                return new BootstrapResult(fallback, lkg, errors, UsedLastKnownGood: true);
            }
        }

        return new BootstrapResult(raw, flat, errors, UsedLastKnownGood: false);
    }

    public static void SaveLastKnownGood(string basePath, IReadOnlyDictionary<string, string?> snapshot) =>
        File.WriteAllText(Path.Combine(basePath, AtmConfiguration.LastKnownGoodFile), JsonSerializer.Serialize(snapshot));
}

/// <summary>Configuration provider that exposes the values of another configuration with every <c>enc:</c> value decrypted in memory only.</summary>
public sealed class DecryptingConfigurationSource(IConfiguration inner, SecretProtector protector) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new DecryptingProvider(inner, protector);

    private sealed class DecryptingProvider : ConfigurationProvider
    {
        private readonly IConfiguration inner;
        private readonly SecretProtector protector;

        public DecryptingProvider(IConfiguration inner, SecretProtector protector)
        {
            this.inner = inner;
            this.protector = protector;
            ChangeToken.OnChange(inner.GetReloadToken, () =>
            {
                Load();
                OnReload();
            });
        }

        public override void Load()
        {
            var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in AtmConfiguration.Flatten(inner))
            {
                data[key] = SecretProtector.IsProtected(value) ? protector.Unprotect(value!) : value;
            }

            Data = data;
        }
    }
}

public static class ConfigurationBuilderExtensions
{
    public static IConfigurationRoot WithDecryption(this IConfiguration raw, SecretProtector protector) =>
        new ConfigurationBuilder().Add(new DecryptingConfigurationSource(raw, protector)).Build();
}
