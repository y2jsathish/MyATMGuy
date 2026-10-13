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
