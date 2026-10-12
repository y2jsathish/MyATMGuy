using System.Diagnostics;
using ATM.Application.Configuration;
using ATM.Infrastructure.Bootstrap;
using ATM.Infrastructure.Devices.Simulator;
using ATM.Infrastructure.Persistence;
using ATM.Presentation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ATM.Integration.Tests;

/// <summary>A complete terminal minus the WPF window: the same bootstrap and registrations the real application uses, on a temp folder.</summary>
public sealed class TestTerminal : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program>? monitoringServer;

    private TestTerminal(string directory, IHost host, PlatformStart start, WebApplicationFactory<Program>? server, TimeSpan startupTime)
    {
        Directory = directory;
        Host = host;
        Start = start;
        monitoringServer = server;
        StartupTime = startupTime;
    }

    public string Directory { get; }

    public IHost Host { get; }

    public PlatformStart Start { get; }

    public TimeSpan StartupTime { get; }

    public IServiceProvider Services => Host.Services;

    public string ConfigFile => Path.Combine(Directory, "appsettings.json");

    public static async Task<TestTerminal> StartAsync(
        string? appsettings = null,
        Action<SimulatorOptions>? simulator = null,
        WebApplicationFactory<Program>? monitoring = null)
    {
        var watch = Stopwatch.StartNew();
        var directory = Path.Combine(Path.GetTempPath(), $"atm-it-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "appsettings.json"), appsettings ?? DefaultSettings(directory, monitoring is not null));

        var start = PlatformBootstrap.Prepare(directory, AtmEnvironment.Dev);
        if (!start.Configuration.IsValid)
        {
            throw new InvalidOperationException("Test configuration is invalid: " + string.Join("; ", start.Configuration.Errors));
        }

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = directory, DisableDefaults = true });
        builder.Configuration.AddConfiguration(start.Decrypted);
        builder.Services.AddPlatform(start, builder.Configuration, simulator).AddPresentation();
        if (monitoring is not null)
        {
            builder.Services.AddHttpClient("IMonitoringClient").ConfigurePrimaryHttpMessageHandler(() => monitoring.Server.CreateHandler());
        }

        var host = builder.Build();
        await host.Services.GetRequiredService<DatabaseMaintenance>().InitializeAsync();
        await host.StartAsync();
        return new TestTerminal(directory, host, start, monitoring, watch.Elapsed);
    }

    public static string DefaultSettings(string directory, bool monitoring) => $$"""
        {
          "Terminal": { "TerminalId": "ATM-0777", "IdleTimeoutSeconds": 60, "DebugFeatures": true },
          "Logging": { "MinimumLevel": "Information", "Directory": {{Json(Path.Combine(directory, "logs"))}} },
          "Database": { "Path": {{Json(Path.Combine(directory, "data", "atm.db"))}}, "BackupDirectory": {{Json(Path.Combine(directory, "data", "backups"))}} },
          "Monitoring": { "BaseUrl": {{Json(monitoring ? "https://monitor.test" : string.Empty)}}, "ApiKey": "{{ApiKey}}", "OutboxPath": {{Json(Path.Combine(directory, "data", "outbox.jsonl"))}} }
        }
        """;

    public const string ApiKey = "integration-key";

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync(TimeSpan.FromSeconds(10));
        Host.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // files still held by a background flush; the temp folder is disposable
        }
    }
}

public sealed class MonitoringServer : WebApplicationFactory<Program>
{
    private readonly string db = Path.Combine(Path.GetTempPath(), $"it-mon-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Monitoring:ConnectionString", $"Data Source={db}");
        builder.UseSetting("Monitoring:ApiKey", TestTerminal.ApiKey);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(db))
        {
            File.Delete(db);
        }
    }
}
