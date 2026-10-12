using System.Text.RegularExpressions;
using ATM.Core.Entities;
using ATM.Core.Errors;
using ATM.Core.ValueObjects;

namespace ATM.Core.Tests;

public class TerminalIdTests
{
    [Fact]
    public void Create_TrimsAndKeepsValidId()
    {
        TerminalId.Create("  ATM-0001 ").Value.Should().Be("ATM-0001");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    public void Create_RejectsInvalidValues(string? value)
    {
        var act = () => TerminalId.Create(value);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_RejectsTooLongValue()
    {
        TerminalId.TryCreate(new string('A', TerminalId.MaxLength + 1), out _).Should().BeFalse();
    }

    [Fact]
    public void Equality_IsByValue()
    {
        TerminalId.Create("ATM-1").Should().Be(TerminalId.Create("ATM-1"));
    }
}

public class MaskedPanTests
{
    private const string TestPan = "4111111111111111";

    [Fact]
    public void FromPan_KeepsOnlyFirstSixAndLastFour()
    {
        MaskedPan.FromPan(TestPan).Value.Should().Be("411111******1111");
    }

    [Fact]
    public void FromPan_AcceptsSpacesAndDashes()
    {
        MaskedPan.FromPan("4111 1111-1111 1111").Value.Should().Be("411111******1111");
    }

    [Fact]
    public void ToString_NeverContainsTheMiddleDigits()
    {
        MaskedPan.FromPan(TestPan).ToString().Should().NotContain("11111111");
    }

    [Theory]
    [InlineData("4111111111111112")]
    [InlineData("12345")]
    [InlineData("41111111111111ab")]
    public void FromPan_RejectsInvalidNumbers(string pan)
    {
        var act = () => MaskedPan.FromPan(pan);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_RoundTripsAMaskedValue()
    {
        var masked = MaskedPan.FromPan(TestPan);

        MaskedPan.Parse(masked.Value).Should().Be(masked);
    }

    [Fact]
    public void Parse_RejectsUnmaskedValues()
    {
        var act = () => MaskedPan.Parse(TestPan);

        act.Should().Throw<FormatException>();
    }
}

public class TransactionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly TerminalId Terminal = TerminalId.Create("ATM-0001");
    private static readonly MaskedPan Card = MaskedPan.FromPan("4111111111111111");

    private static Transaction NewWithdrawal(decimal amount = 100m) =>
        Transaction.Start(Terminal, TransactionType.Withdrawal, amount, Card, Now);

    [Fact]
    public void Start_BeginsPending()
    {
        var tx = NewWithdrawal();

        tx.Status.Should().Be(TransactionStatus.Pending);
        tx.CompletedUtc.Should().BeNull();
    }

    [Fact]
    public void Complete_MovesToCompleted()
    {
        var tx = NewWithdrawal();

        tx.Complete(Now.AddSeconds(5));

        tx.Status.Should().Be(TransactionStatus.Completed);
        tx.CompletedUtc.Should().Be(Now.AddSeconds(5));
    }

    [Fact]
    public void Fail_RecordsTheReason()
    {
        var tx = NewWithdrawal();

        tx.Fail("Dispenser fault", Now.AddSeconds(2));

        tx.Status.Should().Be(TransactionStatus.Failed);
        tx.FailureReason.Should().Be("Dispenser fault");
    }

    [Fact]
    public void Complete_Twice_ThrowsWithCatalogueCode()
    {
        var tx = NewWithdrawal();
        tx.Complete(Now);

        var act = () => tx.Complete(Now);

        act.Should().Throw<AtmException>().Which.Code.Should().Be(ErrorCatalogue.TransactionStateInvalid);
    }

    [Fact]
    public void Fail_AfterComplete_Throws()
    {
        var tx = NewWithdrawal();
        tx.Complete(Now);

        var act = () => tx.Fail("late", Now);

        act.Should().Throw<AtmException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Start_RejectsNonPositiveWithdrawals(int amount)
    {
        var act = () => NewWithdrawal(amount);

        act.Should().Throw<AtmException>();
    }

    [Fact]
    public void Start_AllowsZeroAmountForBalanceInquiry()
    {
        var tx = Transaction.Start(Terminal, TransactionType.BalanceInquiry, 0m, Card, Now);

        tx.Amount.Should().Be(0m);
    }
}

public class ErrorCatalogueTests
{
    [Fact]
    public void Codes_AreUniqueAndWellFormed()
    {
        var codes = ErrorCatalogue.All.Select(e => e.Code).ToList();

        codes.Should().OnlyHaveUniqueItems();
        codes.Should().OnlyContain(c => Regex.IsMatch(c, @"^ATM-\d{4}$"));
    }

    [Fact]
    public void Find_IsCaseInsensitive()
    {
        ErrorCatalogue.Find("atm-4001").Should().Be(ErrorCatalogue.DeviceFault);
    }

    [Fact]
    public void Find_ReturnsNullForUnknownCode()
    {
        ErrorCatalogue.Find("ATM-9999").Should().BeNull();
    }

    [Fact]
    public void AtmException_DefaultsMessageToDescription()
    {
        new AtmException(ErrorCatalogue.DeviceOffline).Message.Should().Be(ErrorCatalogue.DeviceOffline.Description);
    }
}
