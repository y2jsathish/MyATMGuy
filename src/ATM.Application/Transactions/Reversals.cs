using System.Globalization;
using ATM.Application.Audit;
using ATM.Application.Errors;
using ATM.Application.Persistence;
using ATM.Core.Entities;
using ATM.Core.Errors;
using ATM.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace ATM.Application.Transactions;

public sealed class ReversalOptions
{
    /// <summary>Delivery attempts (the first one included) before the reversal is given up and needs manual reconciliation.</summary>
    public int MaxAttempts { get; set; } = 12;

    /// <summary>Wait before the first retry; every further retry doubles it up to <see cref="MaxRetrySeconds"/>.</summary>
    public int FirstRetrySeconds { get; set; } = 30;

    public int MaxRetrySeconds { get; set; } = 900;

    /// <summary>Reversals one run of the retry job delivers at most.</summary>
    public int BatchSize { get; set; } = 20;

    public TimeSpan DelayAfter(int attempts)
    {
        var seconds = FirstRetrySeconds * Math.Pow(2, Math.Max(0, attempts - 1));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetrySeconds));
    }
}

/// <summary>
/// Gets reversals to the host. A reversal is stored first and then sent at once; if the host cannot be reached it is kept and the retry job
/// sends it again with growing waits until the host accepts it. The host must treat a repeated reversal for the same transaction id as already applied.
/// </summary>
/// <summary>Read side of the reversal queue, for the supervisor screen.</summary>
public interface IReversalReport
{
    /// <summary>Reversals that are still waiting or were given up on: the list a person reconciles with the host.</summary>
    Task<IReadOnlyList<ReversalRequest>> UnresolvedAsync(CancellationToken cancellationToken = default);
}

public sealed class ReversalService(
    IHostGateway host,
    IUnitOfWorkFactory unitOfWork,
    IAuditService audit,
    IErrorCounters errors,
    ReversalOptions options,
    TimeProvider clock,
    ILogger<ReversalService> logger) : IReversalReport
{
    public async Task SubmitAsync(Guid transactionId, TerminalId terminal, decimal amount, CancellationToken cancellationToken = default)
    {
        if (amount <= 0)
        {
            return;
        }

        try
        {
            var now = clock.GetUtcNow();
            await using var uow = unitOfWork.Create();
            await uow.Reversals.AddAsync(ReversalRequest.Create(transactionId, terminal, amount, now, now + options.DelayAfter(1)), cancellationToken).ConfigureAwait(false);
            await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without a stored record there is nothing to retry from, so send it once and make the gap loud.
            logger.LogCritical(ex, "Reversal for transaction {TransactionId} could not be stored", transactionId);
            await SendUnstoredAsync(transactionId, amount, cancellationToken).ConfigureAwait(false);
            return;
        }

        await DeliverAsync(transactionId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends every reversal whose wait is over. Returns how many the host accepted.</summary>
    public async Task<int> RetryDueAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Guid> due;
        await using (var uow = unitOfWork.Create())
        {
            due = [.. (await uow.Reversals.DueAsync(clock.GetUtcNow(), options.BatchSize, cancellationToken).ConfigureAwait(false)).Select(r => r.Id)];
        }

        var delivered = 0;
        foreach (var id in due)
        {
            if (await DeliverAsync(id, cancellationToken).ConfigureAwait(false))
            {
                delivered++;
            }
        }

        return delivered;
    }

    public async Task<IReadOnlyList<ReversalRequest>> UnresolvedAsync(CancellationToken cancellationToken = default)
    {
        await using var uow = unitOfWork.Create();
        return await uow.Reversals.UnresolvedAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> DeliverAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var uow = unitOfWork.Create();
        var request = await uow.Reversals.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (request is null || request.State != ReversalState.Pending)
        {
            return false;
        }

        var amountText = request.Amount.ToString("0.00", CultureInfo.InvariantCulture);
        try
        {
            await host.ReverseAsync(request.Id, request.Amount, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var now = clock.GetUtcNow();
            var first = request.Attempts == 0;
            if (request.Attempts + 1 >= options.MaxAttempts)
            {
                request.Abandon(ex.Message, now);
                logger.LogCritical(ex, "Reversal for transaction {TransactionId} was given up after {Attempts} attempts", id, request.Attempts);
                errors.Record(ErrorCatalogue.ReversalFailed);
                await audit.RecordAsync("system", "ReversalAbandoned", $"tx {id}", amountText, AuditCategory.Security, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                request.MarkFailed(ex.Message, now, now + options.DelayAfter(request.Attempts + 1));
                if (first)
                {
                    // The first miss is the alert: the customer may be debited without cash until a retry succeeds.
                    logger.LogCritical(ex, "Reversal for transaction {TransactionId} failed and will be retried", id);
                    errors.Record(ErrorCatalogue.ReversalFailed);
                    await audit.RecordAsync("system", "ReversalFailed", $"tx {id}", amountText, AuditCategory.Security, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }

            await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        request.MarkDelivered(clock.GetUtcNow());
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        await audit.RecordAsync("system", "ReversalSent", $"tx {id}", amountText, AuditCategory.System, cancellationToken: cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task SendUnstoredAsync(Guid id, decimal amount, CancellationToken cancellationToken)
    {
        var amountText = amount.ToString("0.00", CultureInfo.InvariantCulture);
        try
        {
            await host.ReverseAsync(id, amount, cancellationToken).ConfigureAwait(false);
            await audit.RecordAsync("system", "ReversalSent", $"tx {id}", amountText, AuditCategory.System, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogCritical(ex, "Reversal for transaction {TransactionId} failed and was not stored", id);
            errors.Record(ErrorCatalogue.ReversalFailed);
            await audit.RecordAsync("system", "ReversalFailed", $"tx {id}", amountText, AuditCategory.Security, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}
