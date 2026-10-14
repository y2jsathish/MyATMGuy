using System.Globalization;
using System.Security.Cryptography;
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

public enum ServiceResult
{
    Completed,
    CardTimeout,
    PinCancelled,
    PinMismatch,
    PinUnchanged,
    Declined,
    HostUnavailable,
    DeviceFault,
}

/// <summary>What the customer screen needs after a balance inquiry, mini statement or PIN change.</summary>
public sealed record ServiceOutcome(
    ServiceResult Result,
    string Message,
    Guid? TransactionId,
    decimal? Balance,
    IReadOnlyList<HostStatementEntry> Entries,
    bool ReceiptPrinted,
    string? HostResponseCode);

/// <summary>The card and PIN the runner collected, plus the stored transaction the body works for.</summary>
public sealed record CardServiceContext(CardData Card, PinEntryResult Pin, Transaction Transaction);

/// <summary>What a service body returns: the outcome and, optionally, receipt text (masked data only).</summary>
public sealed record CardServiceResult(ServiceOutcome Outcome, string? Receipt);

/// <summary>
/// The shared skeleton of the money-free card services: wait for the card, read the PIN, store the transaction, run the host call,
/// finish and audit, print, and always eject the card. Nothing here moves money, so no reversal is ever needed.
/// </summary>
public sealed class CardServiceRunner(
    ICardReaderPort cardReader,
    IPinPadPort pinPad,
    IReceiptPrinterPort printer,
    IUnitOfWorkFactory unitOfWork,
    IAuditService audit,
    IEventBus events,
    IErrorCounters errors,
    TerminalInfo terminal,
    TransactionOptions options,
    TimeProvider clock,
    ILogger<CardServiceRunner> logger)
{
    public static ServiceOutcome Fail(ServiceResult result, string message, Guid? id = null, string? code = null) =>
        new(result, message, id, null, [], false, code);

    public async Task<ServiceOutcome> RunAsync(
        TransactionType type,
        Func<CardServiceContext, CancellationToken, Task<CardServiceResult>> body,
        CancellationToken cancellationToken)
    {
        using var scope = CorrelationContext.Begin();
        var cardInserted = false;
        try
        {
            CardData? card;
            PinEntryResult pin;
            try
            {
                card = await cardReader.WaitForCardAsync(TimeSpan.FromSeconds(options.CardTimeoutSeconds), cancellationToken).ConfigureAwait(false);
                cardInserted = card is not null;
                if (card is null)
                {
                    return Fail(ServiceResult.CardTimeout, "No card was inserted.");
                }

                pin = await pinPad.ReadPinAsync(options.PinMinLength, options.PinMaxLength, cancellationToken).ConfigureAwait(false);
            }
            catch (AtmException ex)
            {
                return await DeviceFailureAsync(type, ex, null, cancellationToken).ConfigureAwait(false);
            }

            if (!pin.Completed)
            {
                return Fail(ServiceResult.PinCancelled, "PIN entry was not completed.");
            }

            var tx = Transaction.Start(TerminalId.Create(terminal.TerminalId), type, 0m, card.Pan, clock.GetUtcNow());
            await using (var uow = unitOfWork.Create())
            {
                await uow.Transactions.AddAsync(tx, cancellationToken).ConfigureAwait(false);
                await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            await audit.RecordAsync("customer", $"{type}Started", $"tx {tx.Id}", "no amount", AuditCategory.Operator, cancellationToken: cancellationToken).ConfigureAwait(false);

            CardServiceResult result;
            try
            {
                result = await body(new CardServiceContext(card, pin, tx), cancellationToken).ConfigureAwait(false);
            }
            catch (AtmException ex)
            {
                await FinishAsync(type, tx.Id, ex.Message, cancellationToken).ConfigureAwait(false);
                return await DeviceFailureAsync(type, ex, tx.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Host did not answer for transaction {TransactionId}", tx.Id);
                errors.Record(ErrorCatalogue.HostUnavailable);
                await FinishAsync(type, tx.Id, "Host unavailable", cancellationToken).ConfigureAwait(false);
                return Fail(ServiceResult.HostUnavailable, "We could not reach your bank. Please try again later.", tx.Id);
            }

            var completed = result.Outcome.Result == ServiceResult.Completed;
            await FinishAsync(type, tx.Id, completed ? null : $"{result.Outcome.Result} {result.Outcome.HostResponseCode}".Trim(), cancellationToken).ConfigureAwait(false);
            var printed = completed && result.Receipt is not null && await PrintAsync(result.Receipt, tx.Id, cancellationToken).ConfigureAwait(false);
            return result.Outcome with { TransactionId = tx.Id, ReceiptPrinted = printed };
        }
        finally
        {
            if (cardInserted)
            {
                await EjectAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Runs one host call under the configured host timeout.</summary>
    public async Task<T> CallHostAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        using var hostTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        hostTimeout.CancelAfter(TimeSpan.FromSeconds(options.HostTimeoutSeconds));
        return await call(hostTimeout.Token).ConfigureAwait(false);
    }

    /// <summary>Reads another PIN entry, returning null when the customer did not complete it.</summary>
    public async Task<PinEntryResult?> ReadPinAsync(CancellationToken cancellationToken)
    {
        var pin = await pinPad.ReadPinAsync(options.PinMinLength, options.PinMaxLength, cancellationToken).ConfigureAwait(false);
        return pin.Completed ? pin : null;
    }

    public string ReceiptHeader(string title, Transaction tx) =>
        new StringBuilder()
            .AppendLine(title)
            .AppendLine(CultureInfo.InvariantCulture, $"Terminal {tx.Terminal}")
            .AppendLine(CultureInfo.InvariantCulture, $"Card {tx.Card}")
            .AppendLine(CultureInfo.InvariantCulture, $"Reference {tx.Id}")
            .AppendLine(CultureInfo.InvariantCulture, $"{clock.GetUtcNow():u}")
            .ToString();

    private async Task FinishAsync(TransactionType type, Guid id, string? failure, CancellationToken cancellationToken)
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

        await audit.RecordAsync("customer", type.ToString(), $"tx {id}", failure ?? "completed", AuditCategory.Operator, cancellationToken: cancellationToken).ConfigureAwait(false);
        await events.PublishAsync(new TransactionFinished(id, failure is null ? TransactionStatus.Completed : TransactionStatus.Failed), cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> PrintAsync(string text, Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await printer.PrintAsync(text, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AtmException ex)
        {
            logger.LogWarning(ex, "Receipt could not be printed for {TransactionId}", id);
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

    private async Task<ServiceOutcome> DeviceFailureAsync(TransactionType type, AtmException ex, Guid? id, CancellationToken cancellationToken)
    {
        errors.Record(ex.Code);
        await audit.RecordAsync("system", $"{type}DeviceFault", id is null ? "no transaction" : $"tx {id}", ex.Message, AuditCategory.System, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Fail(ServiceResult.DeviceFault, "This ATM cannot complete your request.", id);
    }
}

/// <summary>Shows the customer's available balance.</summary>
public sealed class BalanceInquiryFlow(CardServiceRunner runner, IHostGateway host)
{
    public Task<ServiceOutcome> RunAsync(CancellationToken cancellationToken = default) =>
        runner.RunAsync(TransactionType.BalanceInquiry, async (ctx, ct) =>
        {
            var tx = ctx.Transaction;
            var answer = await runner.CallHostAsync(
                t => host.InquireBalanceAsync(new HostAuthorizationRequest(tx.Id, tx.Terminal, tx.Card, 0m, ctx.Pin.EncryptedPinBlock), t), ct).ConfigureAwait(false);
            if (!answer.Approved || answer.AvailableBalance is null)
            {
                return new CardServiceResult(CardServiceRunner.Fail(ServiceResult.Declined, "Your bank declined this request.", null, answer.ResponseCode), null);
            }

            var receipt = runner.ReceiptHeader("BALANCE INQUIRY", tx) + string.Create(CultureInfo.InvariantCulture, $"Available {answer.AvailableBalance:0.00}\n");
            return new CardServiceResult(
                new ServiceOutcome(ServiceResult.Completed, "Your balance is shown below.", null, answer.AvailableBalance, [], false, answer.ResponseCode),
                receipt);
        }, cancellationToken);
}

/// <summary>Shows and prints the most recent activity on the account.</summary>
public sealed class MiniStatementFlow(CardServiceRunner runner, IHostGateway host, TransactionOptions options)
{
    public Task<ServiceOutcome> RunAsync(CancellationToken cancellationToken = default) =>
        runner.RunAsync(TransactionType.MiniStatement, async (ctx, ct) =>
        {
            var tx = ctx.Transaction;
            var answer = await runner.CallHostAsync(
                t => host.GetMiniStatementAsync(new HostAuthorizationRequest(tx.Id, tx.Terminal, tx.Card, 0m, ctx.Pin.EncryptedPinBlock), t), ct).ConfigureAwait(false);
            if (!answer.Approved)
            {
                return new CardServiceResult(CardServiceRunner.Fail(ServiceResult.Declined, "Your bank declined this request.", null, answer.ResponseCode), null);
            }

            var entries = answer.Entries.Take(Math.Max(1, options.StatementLines)).ToArray();
            var text = new StringBuilder(runner.ReceiptHeader("MINI STATEMENT", tx));
            foreach (var entry in entries)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"{entry.Date:yyyy-MM-dd} {entry.Description} {entry.Amount:0.00}");
            }

            return new CardServiceResult(
                new ServiceOutcome(ServiceResult.Completed, "Your recent activity is shown below.", null, null, entries, false, answer.ResponseCode),
                text.ToString());
        }, cancellationToken);
}

/// <summary>
/// Changes the card PIN. The customer enters the current PIN, then the new one twice. The clear PINs stay in the pin pad:
/// only encrypted blocks and a match token reach this code, and nothing about the PINs is stored, logged or audited.
/// </summary>
public sealed class PinChangeFlow(CardServiceRunner runner, IHostGateway host)
{
    public Task<ServiceOutcome> RunAsync(CancellationToken cancellationToken = default) =>
        runner.RunAsync(TransactionType.PinChange, async (ctx, ct) =>
        {
            var tx = ctx.Transaction;
            var newPin = await runner.ReadPinAsync(ct).ConfigureAwait(false);
            var confirm = newPin is null ? null : await runner.ReadPinAsync(ct).ConfigureAwait(false);
            if (newPin is null || confirm is null)
            {
                return new CardServiceResult(CardServiceRunner.Fail(ServiceResult.PinCancelled, "PIN entry was not completed."), null);
            }

            if (!CryptographicOperations.FixedTimeEquals(newPin.MatchToken.Span, confirm.MatchToken.Span))
            {
                return new CardServiceResult(CardServiceRunner.Fail(ServiceResult.PinMismatch, "The two new PINs did not match. Your PIN was not changed."), null);
            }

            if (CryptographicOperations.FixedTimeEquals(ctx.Pin.MatchToken.Span, newPin.MatchToken.Span))
            {
                return new CardServiceResult(CardServiceRunner.Fail(ServiceResult.PinUnchanged, "Choose a PIN different from your current one."), null);
            }

            var answer = await runner.CallHostAsync(
                t => host.ChangePinAsync(new HostPinChangeRequest(tx.Id, tx.Terminal, tx.Card, ctx.Pin.EncryptedPinBlock, newPin.EncryptedPinBlock), t), ct).ConfigureAwait(false);
            return answer.Approved
                ? new CardServiceResult(new ServiceOutcome(ServiceResult.Completed, "Your PIN was changed.", null, null, [], false, answer.ResponseCode), null)
                : new CardServiceResult(CardServiceRunner.Fail(ServiceResult.Declined, "Your bank did not change the PIN.", null, answer.ResponseCode), null);
        }, cancellationToken);
}
