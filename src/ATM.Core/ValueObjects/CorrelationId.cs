namespace ATM.Core.ValueObjects;

/// <summary>Ties together every log, audit and error record produced by one operation.</summary>
public readonly record struct CorrelationId(Guid Value)
{
    public static CorrelationId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}
