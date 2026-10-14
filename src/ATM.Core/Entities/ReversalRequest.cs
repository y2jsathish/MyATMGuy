using ATM.Core.Errors;
using ATM.Core.ValueObjects;

namespace ATM.Core.Entities;

public enum ReversalState
{
    Pending,
    Delivered,
    Abandoned,
}

/// <summary>
/// A reversal the host must receive after an approved withdrawal did not end in cash. It is stored before the first delivery attempt,
/// so a crash or a dead host line never loses it. It ends Delivered once the host accepts it, or Abandoned when the attempts run out
/// and a person has to reconcile the transaction by hand.
/// </summary>
public sealed class ReversalRequest
{
    private const int MaxErrorLength = 512;

    private ReversalRequest(Guid id, TerminalId terminal, decimal amount, DateTimeOffset createdUtc, DateTimeOffset nextAttemptUtc)
    {
        Id = id;
        Terminal = terminal;
        Amount = amount;
        CreatedUtc = createdUtc;
        NextAttemptUtc = nextAttemptUtc;
        State = ReversalState.Pending;
    }

    /// <summary>The id of the transaction being reversed; the host uses it to ignore a reversal it already applied.</summary>
    public Guid Id { get; }

    public TerminalId Terminal { get; }

    public decimal Amount { get; }

    public DateTimeOffset CreatedUtc { get; }

    public ReversalState State { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset NextAttemptUtc { get; private set; }

    public DateTimeOffset? LastAttemptUtc { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset? ResolvedUtc { get; private set; }

    public static ReversalRequest Create(Guid transactionId, TerminalId terminal, decimal amount, DateTimeOffset nowUtc, DateTimeOffset firstRetryUtc)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        if (amount <= 0)
        {
            throw new AtmException(ErrorCatalogue.TransactionStateInvalid, "A reversal needs a positive amount.");
        }

        return new ReversalRequest(transactionId, terminal, amount, nowUtc, firstRetryUtc);
    }

    public void MarkDelivered(DateTimeOffset nowUtc)
    {
        EnsurePending();
        Attempts++;
        LastAttemptUtc = nowUtc;
        LastError = null;
        State = ReversalState.Delivered;
        ResolvedUtc = nowUtc;
    }

    public void MarkFailed(string error, DateTimeOffset nowUtc, DateTimeOffset nextAttemptUtc)
    {
        EnsurePending();
        Attempts++;
        LastAttemptUtc = nowUtc;
        LastError = Truncate(error);
        NextAttemptUtc = nextAttemptUtc;
    }

    public void Abandon(string error, DateTimeOffset nowUtc)
    {
        EnsurePending();
        Attempts++;
        LastAttemptUtc = nowUtc;
        LastError = Truncate(error);
        State = ReversalState.Abandoned;
        ResolvedUtc = nowUtc;
    }

    private static string Truncate(string value) => value.Length <= MaxErrorLength ? value : value[..MaxErrorLength];

    private void EnsurePending()
    {
        if (State != ReversalState.Pending)
        {
            throw new AtmException(ErrorCatalogue.TransactionStateInvalid, $"Reversal is {State}, expected Pending.");
        }
    }
}
