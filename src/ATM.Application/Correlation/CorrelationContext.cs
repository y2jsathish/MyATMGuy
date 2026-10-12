using ATM.Core.ValueObjects;

namespace ATM.Application.Correlation;

/// <summary>Ambient correlation ID that flows across await boundaries and into child tasks.</summary>
public static class CorrelationContext
{
    public const string HeaderName = "X-Correlation-Id";

    private static readonly AsyncLocal<CorrelationId?> Holder = new();

    public static CorrelationId? Current => Holder.Value;

    /// <summary>Starts a scope. Dispose it to restore the previous ID.</summary>
    public static IDisposable Begin(CorrelationId? id = null)
    {
        var previous = Holder.Value;
        Holder.Value = id ?? CorrelationId.New();
        return new Scope(previous);
    }

    private sealed class Scope(CorrelationId? previous) : IDisposable
    {
        public void Dispose() => Holder.Value = previous;
    }
}
