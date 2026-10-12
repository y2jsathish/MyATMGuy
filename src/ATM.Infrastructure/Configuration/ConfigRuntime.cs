using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ATM.Application.Configuration;
using ATM.Infrastructure.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace ATM.Infrastructure.Configuration;

/// <summary>Typed view over the validated configuration currently in force (never an unvalidated candidate).</summary>
public sealed class LiveSettings(ConfigGovernor governor)
{
    public T Get<T>(string section)
        where T : class, new() =>
        new ConfigurationBuilder().AddInMemoryCollection(governor.Current).Build().GetSection(section).Get<T>() ?? new T();
}

/// <summary>
/// Watches the configuration files. Each change is validated first; a good one is applied (log levels change immediately),
/// a bad one is rejected and the last known good stays in force.
/// Reloadable: Logging levels, Terminal idle timeout. Everything else needs a restart.
/// </summary>
public sealed class ConfigReloadService(
    IConfigurationRoot raw,
    ConfigGovernor governor,
    LiveSettings settings,
    LogLevelController levels,
    ILogger<ConfigReloadService> logger,
    ATM.Application.Events.IEventBus? bus = null) : IHostedService, IDisposable
{
    private IDisposable? registration;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        registration = ChangeToken.OnChange(raw.GetReloadToken, () => _ = ReloadAsync());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<ConfigApplyResult> ReloadAsync()
    {
        var result = await governor.ApplyAsync(AtmConfiguration.Flatten(raw), "file reload").ConfigureAwait(false);
        if (result.Applied)
        {
            levels.Apply(settings.Get<LoggingOptions>(LoggingOptions.SectionName));
            logger.LogInformation("Configuration reloaded: {ChangeCount} change(s)", result.Changes.Count);
            if (bus is not null && result.Changes.Count > 0)
            {
                await bus.PublishAsync(new ATM.Application.Events.ConfigurationChanged([.. result.Changes.Select(c => c.Key)], "file reload")).ConfigureAwait(false);
            }
        }
        else
        {
            logger.LogError("Configuration reload rejected, keeping last known good: {Errors}", string.Join("; ", result.Errors));
        }

        return result;
    }

    public void Dispose() => registration?.Dispose();
}

/// <summary>Generates a JSON schema (draft-07) from an options class so editors can validate config files before deployment.</summary>
public static class OptionsSchema
{
    public static JsonObject Generate(string section, Type optionsType)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var property in optionsType.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            properties[property.Name] = Describe(property);
            if (property.GetCustomAttribute<RequiredAttribute>() is not null)
            {
                required.Add(property.Name);
            }
        }

        var body = new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
        if (required.Count > 0)
        {
            body["required"] = required;
        }

        return new JsonObject { ["title"] = section, ["definition"] = body };
    }

    public static string GenerateDocument(params (string Section, Type Type)[] sections)
    {
        var properties = new JsonObject();
        foreach (var (section, type) in sections)
        {
            properties[section] = Generate(section, type)["definition"]!.DeepClone();
        }

        var document = new JsonObject
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["title"] = "ATM Platform configuration",
            ["type"] = "object",
            ["properties"] = properties,
        };
        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject Describe(PropertyInfo property)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var node = new JsonObject();
        if (type == typeof(string))
        {
            node["type"] = "string";
        }
        else if (type == typeof(bool))
        {
            node["type"] = "boolean";
        }
        else if (type == typeof(int) || type == typeof(long))
        {
            node["type"] = "integer";
        }
        else if (type.IsAssignableTo(typeof(System.Collections.IDictionary)))
        {
            node["type"] = "object";
        }
        else
        {
            node["type"] = "string";
        }

        if (property.GetCustomAttribute<RangeAttribute>() is { } range)
        {
            node["minimum"] = JsonValue.Create(Convert.ToInt64(range.Minimum, System.Globalization.CultureInfo.InvariantCulture));
            node["maximum"] = JsonValue.Create(Convert.ToInt64(range.Maximum, System.Globalization.CultureInfo.InvariantCulture));
        }

        if (property.GetCustomAttribute<RegularExpressionAttribute>() is { } pattern)
        {
            node["pattern"] = pattern.Pattern;
        }

        return node;
    }
}
