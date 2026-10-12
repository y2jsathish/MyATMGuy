using ATM.Core.Errors;
using ATM.Core.ValueObjects;

namespace ATM.Core.Entities;

public enum TransactionType
{
    Withdrawal,
    BalanceInquiry,
    Deposit,
    MiniStatement,
    PinChange,
}

public enum TransactionStatus
{
    Pending,
    Completed,
    Failed,
}

/// <summary>One customer transaction. It starts Pending and ends Completed or Failed, exactly once.</summary>
public sealed class Transaction
{
    private Transaction(TerminalId terminal, TransactionType type, decimal amount, MaskedPan card, DateTimeOffset createdUtc)
    {
        Id = Guid.NewGuid();
        Terminal = terminal;
        Type = type;
        Amount = amount;
        Card = card;
        CreatedUtc = createdUtc;
        Status = TransactionStatus.Pending;
    }

    public Guid Id { get; }

    public TerminalId Terminal { get; }

    public TransactionType Type { get; }

    public decimal Amount { get; }

    public MaskedPan Card { get; }

    public TransactionStatus Status { get; private set; }

    public DateTimeOffset CreatedUtc { get; }

    public DateTimeOffset? CompletedUtc { get; private set; }

    public string? FailureReason { get; private set; }

    public static Transaction Start(TerminalId terminal, TransactionType type, decimal amount, MaskedPan card, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(card);

        if (amount < 0 || (type is TransactionType.Withdrawal or TransactionType.Deposit && amount == 0))
        {
            throw new AtmException(ErrorCatalogue.TransactionStateInvalid, "Amount is not valid for this transaction type.");
        }

        return new Transaction(terminal, type, amount, card, nowUtc);
    }

    public void Complete(DateTimeOffset nowUtc)
    {
        EnsurePending();
        Status = TransactionStatus.Completed;
        CompletedUtc = nowUtc;
    }

    public void Fail(string reason, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        EnsurePending();
        Status = TransactionStatus.Failed;
        FailureReason = reason;
        CompletedUtc = nowUtc;
    }

    private void EnsurePending()
    {
        if (Status != TransactionStatus.Pending)
        {
            throw new AtmException(
                ErrorCatalogue.TransactionStateInvalid,
                $"Transaction is {Status}, expected Pending.");
        }
    }
}
