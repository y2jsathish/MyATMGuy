using ATM.Application.Configuration;
using ATM.Application.Devices;
using ATM.Application.Events;
using ATM.Application.Health;
using ATM.Core.Devices;
using ATM.Infrastructure.Configuration;
using ATM.Infrastructure.Persistence;

namespace ATM.Infrastructure.Health;

public sealed class HealthThresholds
{
    public long DiskDegradedFreeBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    public long DiskUnhealthyFreeBytes { get; set; } = 512L * 1024 * 1024;

    public long MemoryDegradedBytes { get; set; } = 600L * 1024 * 1024;

    public long MemoryUnhealthyBytes { get; set; } = 1200L * 1024 * 1024;

    public int BacklogDegraded { get; set; } = 500;

    public int BacklogUnhealthy { get; set; } = 5000;
}

public sealed class DiskSpaceCheck(string path, HealthThresholds thresholds, Func<string, long>? freeBytes = null) : IHealthCheck
{
    public string Name => "Disk";

    public Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
        var free = (freeBytes ?? (r => new DriveInfo(r).AvailableFreeSpace))(root);
        var text = $"{free / (1024 * 1024)} MB free on {root}";
        return Task.FromResult(
            free < thresholds.DiskUnhealthyFreeBytes ? HealthCheckResult.Unhealthy(text)
            : free < thresholds.DiskDegradedFreeBytes ? HealthCheckResult.Degraded(text)
            : HealthCheckResult.Healthy(text));
    }
}

public sealed class MemoryCheck(HealthThresholds thresholds, Func<long>? workingSet = null) : IHealthCheck
{
    public string Name => "Memory";

    public Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var used = (workingSet ?? (() => Environment.WorkingSet))();
        var text = $"{used / (1024 * 1024)} MB in use";
        return Task.FromResult(
            used > thresholds.MemoryUnhealthyBytes ? HealthCheckResult.Unhealthy(text)
            : used > thresholds.MemoryDegradedBytes ? HealthCheckResult.Degraded(text)
            : HealthCheckResult.Healthy(text));
    }
}

public sealed class ConfigurationHealthCheck(ConfigGovernor governor, AtmEnvironment environment) : IHealthCheck
{
    public string Name => "Configuration";

    public Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var errors = AtmConfiguration.Validate(governor.Current, environment);
        return Task.FromResult(errors.Count == 0 ? HealthCheckResult.Healthy($"valid ({environment})") : HealthCheckResult.Unhealthy(string.Join("; ", errors)));
    }
}

public sealed class DatabaseHealthCheck(DatabaseMaintenance maintenance) : IHealthCheck
{
    public string Name => "Database";

    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default) =>
        await maintenance.IntegrityOkAsync(cancellationToken).ConfigureAwait(false) ? HealthCheckResult.Healthy("integrity ok") : HealthCheckResult.Unhealthy("integrity check failed");
}

public sealed class EventBusBacklogCheck(IEventBus bus, HealthThresholds thresholds) : IHealthCheck
{
    public string Name => "EventBus";

    public Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var backlog = bus.Backlog;
        var text = $"{backlog} event(s) waiting";
        return Task.FromResult(
            backlog >= thresholds.BacklogUnhealthy ? HealthCheckResult.Unhealthy(text)
            : backlog >= thresholds.BacklogDegraded ? HealthCheckResult.Degraded(text)
            : HealthCheckResult.Healthy(text));
    }
}

public sealed class DeviceHealthCheck(IDeviceSelfTestService selfTest) : IHealthCheck
{
    public string Name => "Devices";

    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var report = await selfTest.RunAsync(cancellationToken).ConfigureAwait(false);
        var text = string.Join(", ", report.Devices.Select(d => $"{d.Kind}={d.Status}"));
        return report.Overall switch
        {
            DeviceStatus.Ok => HealthCheckResult.Healthy(text),
            DeviceStatus.Degraded => HealthCheckResult.Degraded(text),
            _ => HealthCheckResult.Unhealthy(text),
        };
    }
}
