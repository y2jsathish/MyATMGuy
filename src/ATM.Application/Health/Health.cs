using ATM.Application.Events;

namespace ATM.Application.Health;

public enum HealthState
{
    Healthy,
    Degraded,
    Unhealthy,
}

public sealed record HealthCheckResult(HealthState State, string Message)
{
    public static HealthCheckResult Healthy(string message = "OK") => new(HealthState.Healthy, message);

    public static HealthCheckResult Degraded(string message) => new(HealthState.Degraded, message);

    public static HealthCheckResult Unhealthy(string message) => new(HealthState.Unhealthy, message);
}

public interface IHealthCheck
{
    string Name { get; }

    Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default);
}

public sealed record HealthEntry(string Name, HealthState State, string Message, DateTimeOffset LastRunUtc);

public sealed record HealthReport(HealthState Overall, IReadOnlyList<HealthEntry> Entries, DateTimeOffset GeneratedUtc);

/// <summary>Runs every registered check, keeps the latest report and publishes an event whenever a check changes state.</summary>
public sealed class HealthMonitor(IEnumerable<IHealthCheck> checks, IEventBus bus, TimeProvider clock)
{
    private readonly object sync = new();
    private readonly Dictionary<string, HealthEntry> latest = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<IHealthCheck> all = [.. checks];

    public HealthReport Latest
    {
        get
        {
            lock (sync)
            {
                var entries = latest.Values.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
                return new HealthReport(Worst(entries), entries, clock.GetUtcNow());
            }
        }
    }

    public async Task<HealthReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var results = await Task.WhenAll(all.Select(c => RunOne(c, cancellationToken))).ConfigureAwait(false);
        var changes = new List<HealthStateChanged>();
        lock (sync)
        {
            foreach (var entry in results)
            {
                var previous = latest.TryGetValue(entry.Name, out var old) ? old.State.ToString() : "Unknown";
                if (previous != entry.State.ToString())
                {
                    changes.Add(new HealthStateChanged(entry.Name, previous, entry.State.ToString(), entry.Message));
                }

                latest[entry.Name] = entry;
            }
        }

        foreach (var change in changes.Where(c => c.Previous != "Unknown" || c.Current != nameof(HealthState.Healthy)))
        {
            await bus.PublishAsync(change, cancellationToken).ConfigureAwait(false);
        }

        return Latest;
    }

    private async Task<HealthEntry> RunOne(IHealthCheck check, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var result = await check.CheckAsync(timeout.Token).ConfigureAwait(false);
            return new HealthEntry(check.Name, result.State, result.Message, clock.GetUtcNow());
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new HealthEntry(check.Name, HealthState.Unhealthy, $"Check failed: {ex.Message}", clock.GetUtcNow());
        }
    }

    private static HealthState Worst(IReadOnlyList<HealthEntry> entries) =>
        entries.Count == 0 ? HealthState.Healthy : entries.Max(e => e.State);
}
