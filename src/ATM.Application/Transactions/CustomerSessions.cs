using System.Globalization;

namespace ATM.Application.Transactions;

/// <summary>What the customer screen shows when a service has ended: a title, a short message and optional detail lines.</summary>
public sealed record CustomerSessionResult(bool Succeeded, string Title, string Message, IReadOnlyList<string> Lines);

/// <summary>
/// The one entry point the customer screens use. Each call runs a whole service (card, PIN, host, receipt, card back) and returns text that is safe
/// to show: no device or host internals, no card number, no PIN data.
/// </summary>
public interface ICustomerSessionService
{
    /// <summary>The amounts offered as one-touch withdrawal buttons.</summary>
    IReadOnlyList<decimal> QuickAmounts { get; }

    Task<CustomerSessionResult> WithdrawAsync(decimal amount, CancellationToken cancellationToken = default);

    Task<CustomerSessionResult> BalanceAsync(CancellationToken cancellationToken = default);

    Task<CustomerSessionResult> MiniStatementAsync(CancellationToken cancellationToken = default);

    Task<CustomerSessionResult> ChangePinAsync(CancellationToken cancellationToken = default);
}

public sealed class CustomerSessionService(
    CashWithdrawalFlow withdrawal,
    BalanceInquiryFlow balance,
    MiniStatementFlow miniStatement,
    PinChangeFlow pinChange,
    TransactionOptions options) : ICustomerSessionService
{
    private const string NotCompleted = "Transaction not completed";

    public IReadOnlyList<decimal> QuickAmounts { get; } = [20m, 50m, 100m, 200m];

    public async Task<CustomerSessionResult> WithdrawAsync(decimal amount, CancellationToken cancellationToken = default)
    {
        var outcome = await withdrawal.RunAsync(amount, cancellationToken).ConfigureAwait(false);
        if (outcome.Result != WithdrawalResult.Completed)
        {
            return new CustomerSessionResult(false, NotCompleted, outcome.Message, []);
        }

        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"Amount {outcome.Dispensed:0.00}"),
            outcome.ReceiptPrinted ? "Your receipt is printing." : "No receipt could be printed.",
            "Please take your card.",
        };
        return new CustomerSessionResult(true, "Please take your cash", outcome.Message, lines);
    }

    public async Task<CustomerSessionResult> BalanceAsync(CancellationToken cancellationToken = default)
    {
        var outcome = await balance.RunAsync(cancellationToken).ConfigureAwait(false);
        return outcome.Result != ServiceResult.Completed
            ? new CustomerSessionResult(false, NotCompleted, outcome.Message, [])
            : new CustomerSessionResult(true, "Your balance", outcome.Message, [string.Create(CultureInfo.InvariantCulture, $"Available {outcome.Balance:0.00}"), "Please take your card."]);
    }

    public async Task<CustomerSessionResult> MiniStatementAsync(CancellationToken cancellationToken = default)
    {
        var outcome = await miniStatement.RunAsync(cancellationToken).ConfigureAwait(false);
        if (outcome.Result != ServiceResult.Completed)
        {
            return new CustomerSessionResult(false, NotCompleted, outcome.Message, []);
        }

        var lines = outcome.Entries
            .Take(Math.Max(1, options.StatementLines))
            .Select(e => string.Create(CultureInfo.InvariantCulture, $"{e.Date:yyyy-MM-dd}  {e.Description}  {e.Amount:0.00}"))
            .ToList();
        lines.Add("Please take your card.");
        return new CustomerSessionResult(true, "Recent activity", outcome.Message, lines);
    }

    public async Task<CustomerSessionResult> ChangePinAsync(CancellationToken cancellationToken = default)
    {
        var outcome = await pinChange.RunAsync(cancellationToken).ConfigureAwait(false);
        return outcome.Result != ServiceResult.Completed
            ? new CustomerSessionResult(false, NotCompleted, outcome.Message, [])
            : new CustomerSessionResult(true, "PIN changed", outcome.Message, ["Please take your card."]);
    }
}
