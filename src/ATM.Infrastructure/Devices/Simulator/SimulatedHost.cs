using System.Collections.Concurrent;
using ATM.Application.Transactions;

namespace ATM.Infrastructure.Devices.Simulator;

/// <summary>Scenario switches for the simulated authorization host. Mutable so tests and test apps can inject faults at runtime.</summary>
public sealed class SimulatedHostOptions
{
    public decimal Balance { get; set; } = 1000m;

    /// <summary>When true, requests are declined with <see cref="DeclineCode"/> regardless of balance.</summary>
    public bool DeclineAll { get; set; }

    public string DeclineCode { get; set; } = "05";

    /// <summary>When true the host never answers (the call throws a timeout), as if the network were down.</summary>
    public bool Unreachable { get; set; }

    /// <summary>Recent activity the simulated host returns for a mini statement, newest first.</summary>
    public List<HostStatementEntry> Statement { get; } =
    [
        new(new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero), "Salary", 2500m),
        new(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), "Groceries", -86.40m),
        new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "Utilities", -120m),
    ];

    /// <summary>When true reversals fail, to exercise manual reconciliation.</summary>
    public bool RejectReversals { get; set; }
}

public sealed class SimulatedHostGateway(SimulatedHostOptions options) : IHostGateway
{
    private readonly ConcurrentQueue<(Guid TransactionId, decimal Amount)> reversals = new();

    public IReadOnlyCollection<(Guid TransactionId, decimal Amount)> Reversals => reversals;

    public Task<HostAuthorizationResponse> AuthorizeAsync(HostAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (options.Unreachable)
        {
            throw new TimeoutException("Simulated host did not answer.");
        }

        if (options.DeclineAll)
        {
            return Task.FromResult(new HostAuthorizationResponse(false, options.DeclineCode, null, "Declined by simulator"));
        }

        if (request.Amount > options.Balance)
        {
            return Task.FromResult(new HostAuthorizationResponse(false, "51", options.Balance, "Insufficient funds"));
        }

        options.Balance -= request.Amount;
        return Task.FromResult(new HostAuthorizationResponse(true, "00", options.Balance, null));
    }

    public Task<HostBalanceResponse> InquireBalanceAsync(HostAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        Check(request, cancellationToken);
        return Task.FromResult(options.DeclineAll
            ? new HostBalanceResponse(false, options.DeclineCode, null)
            : new HostBalanceResponse(true, "00", options.Balance));
    }

    public Task<HostStatementResponse> GetMiniStatementAsync(HostAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        Check(request, cancellationToken);
        return Task.FromResult(options.DeclineAll
            ? new HostStatementResponse(false, options.DeclineCode, [])
            : new HostStatementResponse(true, "00", options.Statement.ToArray()));
    }

    public Task<HostPinChangeResponse> ChangePinAsync(HostPinChangeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Unreachable)
        {
            throw new TimeoutException("Simulated host did not answer.");
        }

        if (options.DeclineAll)
        {
            return Task.FromResult(new HostPinChangeResponse(false, options.DeclineCode));
        }

        PinChanges++;
        return Task.FromResult(new HostPinChangeResponse(true, "00"));
    }

    public int PinChanges { get; private set; }

    private void Check(HostAuthorizationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Unreachable)
        {
            throw new TimeoutException("Simulated host did not answer.");
        }
    }

    public Task ReverseAsync(Guid transactionId, decimal amount, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Unreachable || options.RejectReversals)
        {
            throw new TimeoutException("Simulated host did not accept the reversal.");
        }

        options.Balance += amount;
        reversals.Enqueue((transactionId, amount));
        return Task.CompletedTask;
    }
}
