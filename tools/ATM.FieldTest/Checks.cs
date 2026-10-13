using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using ATM.Application.Audit;
using ATM.Application.Configuration;
using ATM.Application.Devices;
using ATM.Application.Events;
using ATM.Application.Health;
using ATM.Application.Persistence;
using ATM.Core.Entities;
using ATM.Core.ValueObjects;
using ATM.Infrastructure.Bootstrap;
using ATM.Infrastructure.Configuration;
using ATM.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ATM.FieldTest;

public static class Checks
{
    public static void Environment(Report report)
    {
        const string S = "Environment";
        report.Add(S, "OS", Outcome.Info, $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}), process {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        report.Add(S, "Machine", Outcome.Info, $"{System.Environment.MachineName}, user {System.Environment.UserName}, {System.Environment.ProcessorCount} logical processors");
        report.Add(S, "Memory", Outcome.Info, $"{GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024} MB available to the process");
        report.Add(S, "Clock", Outcome.Info, $"UTC {DateTime.UtcNow:O}, local zone {TimeZoneInfo.Local.Id}");
        report.Add(S, "Elevated", Outcome.Info, OperatingSystem.IsWindows() ? IsElevated().ToString() : "n/a");

        var root = Path.GetPathRoot(Path.GetTempPath());
        if (root is not null)
        {
            var drive = new DriveInfo(root);
            var freeMb = drive.AvailableFreeSpace / 1024 / 1024;
            report.Add(S, "Free disk", freeMb > 1024 ? Outcome.Pass : Outcome.Warn, $"{freeMb} MB free on {drive.Name}");
        }
    }

    public static void DiskSync(Report report)
    {
        const string S = "Disk";
        var path = Path.Combine(Path.GetTempPath(), $"atm-fsync-{Guid.NewGuid():N}.bin");
        var buffer = new byte[4096];
        var samples = new List<double>();
        try
        {
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            for (var i = 0; i < 200; i++)
            {
                var start = Stopwatch.GetTimestamp();
                file.Write(buffer);
                file.Flush(flushToDisk: true);
                samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
        }
        finally
        {
            File.Delete(path);
        }

        var p95 = Stats.Percentile(samples, 0.95);
        report.Add(S, "fsync latency", p95 <= 20 ? Outcome.Pass : Outcome.Warn, $"p50 {Stats.Percentile(samples, 0.5):0.00} ms, p95 {p95:0.00} ms, max {samples.Max():0.00} ms (database commits use synchronous=FULL, so this drives transaction speed)");
    }

    public static void Secrets(Report report)
    {
        const string S = "Secrets";
        if (!OperatingSystem.IsWindows())
        {
            report.Add(S, "DPAPI key provider", Outcome.Skip, "Windows only");
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), $"atm-keys-{Guid.NewGuid():N}");
        try
        {
            var protector = new SecretProtector(KeyProviderFactory.Create(dir, AtmEnvironment.Prod));
            var sealedValue = protector.Protect("field-test-secret");
            var ok = protector.Unprotect(sealedValue) == "field-test-secret" && SecretProtector.IsProtected(sealedValue);
            report.Add(S, "DPAPI key provider", ok ? Outcome.Pass : Outcome.Fail, ok ? "key created, value encrypted and decrypted" : "round trip mismatch");
        }
        catch (Exception ex)
        {
            report.Add(S, "DPAPI key provider", Outcome.Fail, ex.Message);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [SupportedOSPlatform("windows")]
    public static void Xfs(Report report, string? customPath = null)
    {
        const string S = "XFS";
        var found = 0;
        var views = new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 };
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            roots.Add(NormalizeXfsPath(customPath));
        }

        roots.Add(@"SOFTWARE\XFS");
        foreach (var view in views)
        {
            using var hive = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view);
            foreach (var root in roots)
            {
                using var xfs = hive.OpenSubKey(root);
                if (xfs is null)
                {
                    report.Add(S, $"HKLM\\{root} ({view})", Outcome.Info, "not present");
                    continue;
                }

                found += ReadServices(report, xfs, $"{root} ({view})");
            }

            if (found == 0)
            {
                // Fallback: look for any LOGICAL_SERVICES key a few levels under SOFTWARE, in case the vendor uses another root.
                using var software = hive.OpenSubKey("SOFTWARE");
                foreach (var path in FindKeys(software, "LOGICAL_SERVICES", 3, "SOFTWARE"))
                {
                    var parent = path[..path.LastIndexOf('\\')];
                    if (roots.Contains(parent, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    using var xfs = hive.OpenSubKey(parent);
                    if (xfs is not null)
                    {
                        report.Add(S, $"discovered root HKLM\\{parent} ({view})", Outcome.Info, "found a LOGICAL_SERVICES key; use --xfs-path to pin it");
                        found += ReadServices(report, xfs, $"{parent} ({view})");
                    }
                }
            }
        }

        report.Add(S, "summary", found > 0 ? Outcome.Pass : Outcome.Info, found > 0 ? $"{found} logical services found. Send this report so the Phase 2 adapters can target them." : "no XFS logical services registered. Try --xfs-path \"HKLM\\SOFTWARE\\<vendor>\\XFS\" with your registry path. The device adapters in Phase 2 need XFS (or XFS4IoT) services installed on the machine.");
    }

    private static string NormalizeXfsPath(string path)
    {
        var p = path.Trim().Trim('"').Replace('/', '\\');
        foreach (var prefix in new[] { "HKEY_LOCAL_MACHINE\\", "HKLM\\" })
        {
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                p = p[prefix.Length..];
            }
        }

        return p.Trim('\\');
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IEnumerable<string> FindKeys(Microsoft.Win32.RegistryKey? key, string name, int depth, string path)
    {
        if (key is null || depth < 0)
        {
            yield break;
        }

        string[] children;
        try
        {
            children = key.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            yield break;
        }

        foreach (var child in children)
        {
            var childPath = path + "\\" + child;
            if (string.Equals(child, name, StringComparison.OrdinalIgnoreCase))
            {
                yield return childPath;
                continue;
            }

            Microsoft.Win32.RegistryKey? sub = null;
            try
            {
                sub = key.OpenSubKey(child);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }

            using (sub)
            {
                foreach (var hit in FindKeys(sub, name, depth - 1, childPath))
                {
                    yield return hit;
                }
            }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int ReadServices(Report report, Microsoft.Win32.RegistryKey xfs, string label)
    {
        var count = 0;
        using var logical = xfs.OpenSubKey("LOGICAL_SERVICES");
        foreach (var name in logical?.GetSubKeyNames() ?? [])
        {
            using var key = logical!.OpenSubKey(name);
            var provider = key?.GetValue("provider")?.ToString() ?? "?";
            var klass = key?.GetValue("class")?.ToString() ?? "?";
            using var providerKey = xfs.OpenSubKey(@"SERVICE_PROVIDERS\" + provider);
            var dll = providerKey?.GetValue("dllname")?.ToString() ?? "?";
            report.Add("XFS", $"logical service {name} ({label})", Outcome.Info, $"class {klass}, provider {provider}, dll {dll}");
            count++;
        }

        return count;
    }

    public static async Task PlatformAsync(Report report, string terminalId)
    {
        const string S = "Platform";
        var dir = Path.Combine(Path.GetTempPath(), $"atm-field-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var watch = Stopwatch.StartNew();
        IHost? host = null;
        try
        {
            File.WriteAllText(Path.Combine(dir, "appsettings.json"), Settings(dir, terminalId));
            var start = PlatformBootstrap.Prepare(dir, AtmEnvironment.Dev);
            if (!start.Configuration.IsValid)
            {
                report.Add(S, "configuration", Outcome.Fail, string.Join("; ", start.Configuration.Errors));
                return;
            }

            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = dir, DisableDefaults = true });
            builder.Configuration.AddConfiguration(start.Decrypted);
            builder.Services.AddPlatform(start, builder.Configuration, null);
            host = builder.Build();
            await host.Services.GetRequiredService<DatabaseMaintenance>().InitializeAsync();
            await host.StartAsync();
            watch.Stop();
            report.Add(S, "start to ready", watch.Elapsed.TotalSeconds < 10 ? Outcome.Pass : Outcome.Fail, $"{watch.Elapsed.TotalMilliseconds:0} ms (target under 10 s)");

            var health = await host.Services.GetRequiredService<HealthMonitor>().RunAsync();
            report.Add(S, "health checks", health.Overall == HealthState.Healthy ? Outcome.Pass : Outcome.Warn, string.Join("; ", health.Entries.Select(e => $"{e.Name}={e.State}")));

            var self = await host.Services.GetRequiredService<IDeviceSelfTestService>().RunAsync();
            report.Add(S, "device self-test (simulators)", self.Overall == ATM.Core.Devices.DeviceStatus.Ok ? Outcome.Pass : Outcome.Warn, string.Join("; ", self.Devices.Select(d => $"{d.Kind}={d.Status}")));

            var withdrawal = await host.Services.GetRequiredService<ATM.Application.Transactions.CashWithdrawalFlow>().RunAsync(100m);
            report.Add(S, "withdrawal flow (simulated devices and host)", withdrawal.Result == ATM.Application.Transactions.WithdrawalResult.Completed ? Outcome.Pass : Outcome.Fail, $"{withdrawal.Result}, dispensed {withdrawal.Dispensed}, receipt printed {withdrawal.ReceiptPrinted}");

            await TransactionsAsync(report, host.Services, terminalId);
            await EventBusAsync(report, host.Services);
        }
        catch (Exception ex)
        {
            report.Add(S, "run", Outcome.Fail, ex.ToString());
        }
        finally
        {
            if (host is not null)
            {
                var stop = Stopwatch.StartNew();
                await host.StopAsync(TimeSpan.FromSeconds(10));
                report.Add(S, "shutdown", stop.Elapsed.TotalSeconds < 10 ? Outcome.Pass : Outcome.Warn, $"{stop.Elapsed.TotalMilliseconds:0} ms");
                host.Dispose();
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDelete(dir);
        }
    }

    public static async Task SoakAsync(Report report, string terminalId, int minutes)
    {
        const string S = "Soak";
        var dir = Path.Combine(Path.GetTempPath(), $"atm-soak-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "appsettings.json"), Settings(dir, terminalId));
        var start = PlatformBootstrap.Prepare(dir, AtmEnvironment.Dev);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = dir, DisableDefaults = true });
        builder.Configuration.AddConfiguration(start.Decrypted);
        builder.Services.AddPlatform(start, builder.Configuration, null);
        using var host = builder.Build();
        await host.Services.GetRequiredService<DatabaseMaintenance>().InitializeAsync();
        await host.StartAsync();

        var factory = host.Services.GetRequiredService<IUnitOfWorkFactory>();
        var audit = host.Services.GetRequiredService<IAuditService>();
        var health = host.Services.GetRequiredService<HealthMonitor>();
        using var process = Process.GetCurrentProcess();
        var baseline = Sample(process);
        var errors = 0;
        var transactions = 0;
        var end = DateTime.UtcNow.AddMinutes(minutes);
        var nextSample = DateTime.UtcNow.AddMinutes(1);
        report.Add(S, "start", Outcome.Info, $"{minutes} minute(s); baseline working set {baseline.WorkingSetMb} MB, {baseline.Threads} threads, {baseline.Handles} handles");

        while (DateTime.UtcNow < end)
        {
            try
            {
                await InsertOneAsync(factory, audit, terminalId, transactions);
                transactions++;
                if (transactions % 50 == 0)
                {
                    await health.RunAsync();
                }
            }
            catch (Exception ex)
            {
                errors++;
                report.Add(S, "error", Outcome.Fail, ex.Message);
            }

            if (DateTime.UtcNow >= nextSample)
            {
                var s = Sample(process);
                report.Add(S, "sample", Outcome.Info, $"{transactions} transactions, working set {s.WorkingSetMb} MB, {s.Threads} threads, {s.Handles} handles");
                nextSample = DateTime.UtcNow.AddMinutes(1);
            }

            await Task.Delay(100);
        }

        var last = Sample(process);
        var verification = await audit.VerifyAsync();
        var growth = last.WorkingSetMb - baseline.WorkingSetMb;
        report.Add(S, "transactions", errors == 0 ? Outcome.Pass : Outcome.Fail, $"{transactions} completed, {errors} errors");
        report.Add(S, "audit chain", verification.IsValid ? Outcome.Pass : Outcome.Fail, $"{verification.EntryCount} entries, valid {verification.IsValid}");
        report.Add(S, "memory growth", growth < 100 ? Outcome.Pass : Outcome.Warn, $"{growth} MB over the run, threads {baseline.Threads}->{last.Threads}, handles {baseline.Handles}->{last.Handles}");
        await host.StopAsync(TimeSpan.FromSeconds(10));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TryDelete(dir);
    }

    private static async Task TransactionsAsync(Report report, IServiceProvider services, string terminalId)
    {
        const string S = "Platform";
        var factory = services.GetRequiredService<IUnitOfWorkFactory>();
        var audit = services.GetRequiredService<IAuditService>();
        var samples = new List<double>();
        for (var i = 0; i < 300; i++)
        {
            var start = Stopwatch.GetTimestamp();
            await InsertOneAsync(factory, audit, terminalId, i);
            samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        var p95 = Stats.Percentile(samples.Skip(20).ToList(), 0.95);
        report.Add(S, "transaction + audit p95", p95 < 40 ? Outcome.Pass : Outcome.Warn, $"{p95:0.00} ms per transaction with its audit entry (database insert target is 20 ms, audit adds one more commit)");
        var verification = await audit.VerifyAsync();
        report.Add(S, "audit chain", verification.IsValid ? Outcome.Pass : Outcome.Fail, $"{verification.EntryCount} entries, valid {verification.IsValid}");
    }

    private static async Task EventBusAsync(Report report, IServiceProvider services)
    {
        var bus = services.GetRequiredService<IEventBus>();
        var latencies = new List<double>();
        bus.Subscribe<ConfigurationChanged>((e, _) =>
        {
            lock (latencies)
            {
                latencies.Add(Stopwatch.GetElapsedTime(long.Parse(e.Source, CultureInfo.InvariantCulture)).TotalMilliseconds);
            }

            return Task.CompletedTask;
        });
        for (var i = 0; i < 1000; i++)
        {
            await bus.PublishAsync(new ConfigurationChanged(["k"], Stopwatch.GetTimestamp().ToString(CultureInfo.InvariantCulture)));
        }

        await services.GetRequiredService<InProcessEventBus>().DrainAsync();
        var p95 = Stats.Percentile(latencies, 0.95);
        report.Add("Platform", "event bus dispatch p95", p95 < 5 ? Outcome.Pass : Outcome.Warn, $"{p95:0.000} ms over {latencies.Count} events (target under 5 ms)");
    }

    private static async Task InsertOneAsync(IUnitOfWorkFactory factory, IAuditService audit, string terminalId, int index)
    {
        var tx = Transaction.Start(TerminalId.Create(terminalId), TransactionType.Withdrawal, 20m, MaskedPan.FromPan("4111111111111111"), DateTimeOffset.UtcNow);
        tx.Complete(DateTimeOffset.UtcNow);
        await using (var uow = factory.Create())
        {
            await uow.Transactions.AddAsync(tx);
            await uow.CommitAsync();
        }

        await audit.RecordAsync("fieldtest", "Withdrawal", $"tx {tx.Id}", "completed " + index.ToString(CultureInfo.InvariantCulture), AuditCategory.Operator);
    }

    private static (long WorkingSetMb, int Threads, int Handles) Sample(Process process)
    {
        process.Refresh();
        return (process.WorkingSet64 / 1024 / 1024, process.Threads.Count, process.HandleCount);
    }

    private static string Settings(string dir, string terminalId) => $$"""
        {
          "Terminal": { "TerminalId": {{System.Text.Json.JsonSerializer.Serialize(terminalId)}}, "IdleTimeoutSeconds": 60, "DebugFeatures": true },
          "Logging": { "MinimumLevel": "Information", "Directory": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(dir, "logs"))}} },
          "Database": { "Path": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(dir, "data", "atm.db"))}}, "BackupDirectory": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(dir, "data", "backups"))}} },
          "Monitoring": { "BaseUrl": "", "ApiKey": "", "OutboxPath": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(dir, "data", "outbox.jsonl"))}} }
        }
        """;

    [SupportedOSPlatform("windows")]
    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // a background flush may still hold a file; the folder is disposable
        }
        catch (UnauthorizedAccessException)
        {
            // same
        }
    }
}
