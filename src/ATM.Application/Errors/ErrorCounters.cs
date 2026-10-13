using System.Collections.Concurrent;
using ATM.Core.Errors;

namespace ATM.Application.Errors;

/// <summary>Counts catalogued errors so health checks and monitoring can report them.</summary>
public interface IErrorCounters
{
    void Record(ErrorCode code);

    IReadOnlyDictionary<string, int> Snapshot();

    int Total { get; }
}

public sealed class ErrorCounters : IErrorCounters
{
    private readonly ConcurrentDictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);

    public int Total => counts.Values.Sum();

    public void Record(ErrorCode code)
    {
        ArgumentNullException.ThrowIfNull(code);
        counts.AddOrUpdate(code.Code, 1, (_, n) => n + 1);
    }

    public IReadOnlyDictionary<string, int> Snapshot() => new Dictionary<string, int>(counts);
}
