using System.Globalization;
using System.Text;
using ATM.Application.Audit;
using ATM.Application.Correlation;
using ATM.Application.Devices;
using ATM.Application.Errors;
using ATM.Application.Events;
using ATM.Application.Persistence;
using ATM.Application.Terminal;
using ATM.Core.Entities;
using ATM.Core.Errors;
using ATM.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace ATM.Application.Transactions;

public enum WithdrawalResult
{
    Completed,
    InvalidAmount,
    CardTimeout,
    PinCancelled,
    Declined,
    HostUnavailable,
    DispenseFailed,
    DeviceFault,
}

/// <summary>What the customer screen needs: the result, a safe message and what actually happened to the cash and the receipt.</summary>
public sealed record WithdrawalOutcome(WithdrawalResult Result, string Message, Guid? TransactionId, decimal Dispensed, bool ReceiptPrinted, string? HostResponseCode);

/// <summary>
/// One cash withdrawal from card to cash. The transaction is stored Pending before the host is asked, so a crash never loses it,
/// and any failure after an approval sends a reversal to the host.
/// </summary>
public sealed class CashWithdrawalFlow(
    ICardReaderPort cardReader,
    IPinPadPort pinPad,
    ICashDispenserPort dispenser,
    IReceiptPrinterPort printer,
    IHostGateway host,
    IUnitOfWorkFactory unitOfWork,
    IAuditService audit,
    IEventBus events,
    IErrorCounters errors,
    TerminalInfo terminal,
    TransactionOptions options,
    TimeProvider clock,
    ILogger<CashWithdrawalFlow> logger)
{
    public async Task<WithdrawalOutcome> RunAsync(decimal amount, CancellationToken cancellationToken = default)
    {
        using var scope = CorrelationContext.Begin();
        if (!IsValidAmount(amount))
        {
            return new WithdrawalOutcome(WithdrawalResult.InvalidAmount, $"Choose an amount from {options.MinimumAmount:0} to {options.MaximumAmount:0} in multiples of {options.Multiple:0}.", null, 0m, false, null);
        }

        var cardInserted = false;
        try
        {
            CardData? card;
            try
            {
                card = await cardReader.WaitForCardAsync(TimeSpan.FromSeconds(options.CardTimeoutSeconds), cancellationToken).ConfigureAwait(false);
                cardInserted = card is not null;
            }
            catch (AtmException ex)
            {
                return await DeviceFailureAsync(ex, null, cancellationToken).ConfigureAwait(false);
            }

            if (card is null)
            {
                return new WithdrawalOutcome(WithdrawalResult.CardTimeout, "No card was inserted.", null, 0m, false, null);
            }

            PinEntryResult pin;
            try
            {
                pin = await pinPad.ReadPinAsync(options.PinMinLength, options.PinMaxLength, cancellationToken).ConfigureAwait(false);
            }
            catch (AtmException ex)
            {
                return await DeviceFailureAsync(ex, null, cancellationToken).ConfigureAwait(false);
            }

            if (!pin.Completed)
            {
                return new WithdrawalOutcome(WithdrawalResult.PinCancelled, "PIN entry was not completed.", null, 0m, false, null);
            }

            var tx = Transaction.Start(TerminalId.Create(terminal.TerminalId), TransactionType.Withdrawal, amount, card.Pan, clock.GetUtcNow());
            await StoreAsync(uow => uow.Transactions.AddAsync(tx, cancellationToken), cancellationToken).ConfigureAwait(false);
            await audit.RecordAsync("customer", "WithdrawalStarted", $"tx {tx.Id}", $"amount {amount.ToString("0.00", CultureInfo.InvariantCulture)}", AuditCategory.Operator, cancellationToken: cancellationToken).ConfigureAwait(false);

            HostAuthorizationResponse approval;
            try
            {
                using var hostTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                hostTimeout.CancelAfter(TimeSpan.FromSeconds(options.HostTimeoutSeconds));
                approval = await host.AuthorizeAsync(new HostAuthorizationRequest(tx.Id, tx.Terminal, tx.Card, amount, pin.EncryptedPinBlock), hostTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // The host may have approved and we never heard: reverse to be safe.
                logger.LogWarning(ex, "Host did not answer for transaction {TransactionId}", tx.Id);
                errors.Record(ErrorCatalogue.HostUnavailable);
                await ReverseAsync(tx.Id, amount, cancellationToken).ConfigureAwait(false);
                await FinishAsync(tx.Id, null, "Host unavailable", cancellationToken).ConfigureAwait(false);
                return new WithdrawalOutcome(WithdrawalResult.HostUnavailable, "We could not reach your bank. No money was taken.", tx.Id, 0m, false, null);
            }

            if (!approval.Approved)
            {
                await FinishAsync(tx.Id, null, $"Declined {approval.ResponseCode}", cancellationToken).ConfigureAwait(false);
                return new WithdrawalOutcome(WithdrawalResult.Declined, "Your bank declined this request.", tx.Id, 0m, false, approval.ResponseCode);
            }

            DispenseResult cash;
            try
            {
                cash = await dispenser.DispenseAsync(amount, cancellationToken).ConfigureAwait(false);
            }
            catch (AtmException ex)
            {
                cash = new DispenseResult(false, 0m, ex.Message);
            }

            if (!cash.Success || cash.Dispensed < amount)
            {
                errors.Record(ErrorCatalogue.DispenseFailed);
                await ReverseAsync(tx.Id, amount - cash.Dispensed, cancellationToken).ConfigureAwait(false);
                await FinishAsync(tx.Id, null, $"Dispense failed after approval: {cash.Error ?? "short dispense"}", cancellationToken).ConfigureAwait(false);
                return new WithdrawalOutcome(WithdrawalResult.DispenseFailed, "We could not give you the cash. Your account will not be charged for it.", tx.Id, cash.Dispensed, false, approval.ResponseCode);
            }

            await FinishAsync(tx.Id, cash.Dispensed, null, cancellationToken).ConfigureAwait(false);
            var printed = await PrintReceiptAsync(tx, cash.Dispensed, cancellationToken).ConfigureAwait(false);
            return new WithdrawalOutcome(WithdrawalResult.Completed, "Please take your cash.", tx.Id, cash.Dispensed, printed, approval.ResponseCode);
        }
        finally
        {
            if (cardInserted)
            {
                await EjectAsync().ConfigureAwait(false);
            }
        }
    }

    private bool IsValidAmount(decimal amount) =>
        amount >= options.MinimumAmount && amount <= options.MaximumAmount && options.Multiple > 0 && amount % options.Multiple == 0;

    private async Task StoreAsync(Func<IUnitOfWork, Task> change, CancellationToken cancellationToken)
    {
        await using var uow = unitOfWork.Create();
        await change(uow).ConfigureAwait(false);
        await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task FinishAsync(Guid id, decimal? dispensed, string? failure, CancellationToken cancellationToken)
    {
        await using (var uow = unitOfWork.Create())
        {
            var tx = await uow.Transactions.GetAsync(id, cancellationToken).ConfigureAwait(false)
                ?? throw new AtmException(ErrorCatalogue.TransactionStateInvalid, "Transaction disappeared.");
            if (failure is null)
            {
                tx.Complete(clock.GetUtcNow());
            }
            else
            {
                tx.Fail(failure, clock.GetUtcNow());
            }

            await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await audit.RecordAsync("customer", "Withdrawal", $"tx {id}", failure ?? $"completed {dispensed?.ToString("0.00", CultureInfo.InvariantCulture)}", AuditCategory.Operator, cancellationToken: cancellationToken).ConfigureAwait(false);
        await events.PublishAsync(new TransactionFinished(id, failure is null ? TransactionStatus.Completed : TransactionStatus.Failed), cancellationToken).ConfigureAwait(false);
    }

    private async Task ReverseAsync(Guid id, decimal amount, CancellationToken cancellationToken)
    {
        if (amount <= 0)
        {
            return;
        }

        try
        {
            await host.ReverseAsync(id, amount, cancellationToken).ConfigureAwait(false);
            await audit.RecordAsync("system", "ReversalSent", $"tx {id}", amount.ToString("0.00", CultureInfo.InvariantCulture), AuditCategory.System, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Needs manual reconciliation: the customer may have been debited without cash.
            logger.LogCritical(ex, "Reversal for transaction {TransactionId} failed", id);
            errors.Record(ErrorCatalogue.ReversalFailed);
            await audit.RecordAsync("system", "ReversalFailed", $"tx {id}", amount.ToString("0.00", CultureInfo.InvariantCulture), AuditCategory.Security, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> PrintReceiptAsync(Transaction tx, decimal dispensed, CancellationToken cancellationToken)
    {
        var text = new StringBuilder()
            .AppendLine("CASH WITHDRAWAL")
            .AppendLine(CultureInfo.InvariantCulture, $"Terminal {tx.Terminal}")
            .AppendLine(CultureInfo.InvariantCulture, $"Card {tx.Card}")
            .AppendLine(CultureInfo.InvariantCulture, $"Amount {dispensed:0.00}")
            .AppendLine(CultureInfo.InvariantCulture, $"Reference {tx.Id}")
            .AppendLine(CultureInfo.InvariantCulture, $"{clock.GetUtcNow():u}")
            .ToString();
        try
        {
            await printer.PrintAsync(text, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AtmException ex)
        {
            // The customer already has the cash; a missing receipt never undoes the withdrawal.
            logger.LogWarning(ex, "Receipt could not be printed for {TransactionId}", tx.Id);
            return false;
        }
    }

    private async Task EjectAsync()
    {
        try
        {
            await cardReader.EjectAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (AtmException ex)
        {
            logger.LogError(ex, "Card could not be ejected");
            await audit.RecordAsync("system", "CardEjectFailed", "card reader", ex.Message, AuditCategory.System).ConfigureAwait(false);
        }
    }

    private async Task<WithdrawalOutcome> DeviceFailureAsync(AtmException ex, Guid? id, CancellationToken cancellationToken)
    {
        errors.Record(ex.Code);
        await audit.RecordAsync("system", "WithdrawalDeviceFault", id is null ? "no transaction" : $"tx {id}", ex.Message, AuditCategory.System, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new WithdrawalOutcome(WithdrawalResult.DeviceFault, "This ATM cannot complete your request. No money was taken.", id, 0m, false, null);
    }
}
