using ATM.Core.Entities;
using ATM.Core.Errors;
using ATM.Core.ValueObjects;
using ATM.TestApp.Common;

var runner = new CheckRunner("ATM.Core");
var now = DateTimeOffset.UtcNow;
var terminal = TerminalId.Create("ATM-0001");
var card = MaskedPan.FromPan("4111111111111111");

runner.Check("TerminalId trims and validates", () =>
{
    Expect.Equal("ATM-0001", TerminalId.Create(" ATM-0001 ").Value);
    Expect.Throws<ArgumentException>(() => TerminalId.Create("bad id"));
});

runner.Check("MaskedPan keeps only first six and last four digits", () =>
{
    Expect.Equal("411111******1111", card.Value);
    Expect.True(!card.ToString().Contains("11111111", StringComparison.Ordinal), "Middle digits leaked into the masked value.");
});

runner.Check("MaskedPan rejects a number that fails the Luhn check", () =>
    Expect.Throws<ArgumentException>(() => MaskedPan.FromPan("4111111111111112")));

runner.Check("Transaction goes Pending to Completed exactly once", () =>
{
    var tx = Transaction.Start(terminal, TransactionType.Withdrawal, 100m, card, now);
    Expect.Equal(TransactionStatus.Pending, tx.Status);
    tx.Complete(now);
    Expect.Equal(TransactionStatus.Completed, tx.Status);
    var error = Expect.Throws<AtmException>(() => tx.Complete(now));
    Expect.Equal("ATM-5001", error.Code.Code, "Error code");
});

runner.Check("Failed transaction keeps its reason", () =>
{
    var tx = Transaction.Start(terminal, TransactionType.Withdrawal, 20m, card, now);
    tx.Fail("Dispenser fault", now);
    Expect.Equal("Dispenser fault", tx.FailureReason);
});

runner.Check("Error catalogue codes are unique", () =>
{
    var distinct = ErrorCatalogue.All.Select(e => e.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    Expect.Equal(ErrorCatalogue.All.Count, distinct, "Distinct code count");
});

return runner.Complete(args);
