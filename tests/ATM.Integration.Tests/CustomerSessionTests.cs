using ATM.Application.Transactions;
using ATM.Infrastructure.Devices.Simulator;
using Microsoft.Extensions.DependencyInjection;

namespace ATM.Integration.Tests;

/// <summary>The text the customer screens show, produced by the real flows on the simulated devices and host.</summary>
public class CustomerSessionTests
{
    private static ICustomerSessionService Sessions(TestTerminal t) => t.Services.GetRequiredService<ICustomerSessionService>();

    [Fact]
    public async Task Withdrawal_ShowsTheAmount_TheReceiptState_AndTheCardPrompt()
    {
        await using var terminal = await TestTerminal.StartAsync();

        var result = await Sessions(terminal).WithdrawAsync(100m);

        result.Succeeded.Should().BeTrue();
        result.Title.Should().Be("Please take your cash");
        result.Lines.Should().Equal("Amount 100.00", "Your receipt is printing.", "Please take your card.");
    }

    [Fact]
    public async Task Withdrawal_OfTooMuch_IsDeclinedWithoutHostInternals()
    {
        await using var terminal = await TestTerminal.StartAsync();
        terminal.Services.GetRequiredService<SimulatedHostOptions>().Balance = 20m;

        var result = await Sessions(terminal).WithdrawAsync(100m);

        result.Succeeded.Should().BeFalse();
        result.Title.Should().Be("Transaction not completed");
        result.Message.Should().NotContain("51").And.NotContain("Insufficient funds");
        result.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task Balance_ShowsTheAvailableAmount()
    {
        await using var terminal = await TestTerminal.StartAsync();

        var result = await Sessions(terminal).BalanceAsync();

        result.Succeeded.Should().BeTrue();
        result.Lines.Should().Contain("Available 1000.00");
    }

    [Fact]
    public async Task MiniStatement_ListsRecentActivityNewestFirst()
    {
        await using var terminal = await TestTerminal.StartAsync();

        var result = await Sessions(terminal).MiniStatementAsync();

        result.Succeeded.Should().BeTrue();
        result.Lines[0].Should().Be("2026-01-03  Salary  2500.00");
        result.Lines[1].Should().Be("2026-01-02  Groceries  -86.40");
        result.Lines[^1].Should().Be("Please take your card.");
    }

    [Fact]
    public async Task PinChange_ConfirmsWithoutShowingAnyPin()
    {
        await using var terminal = await TestTerminal.StartAsync();
        var pins = terminal.Services.GetRequiredService<SimulatorOptions>().PinScript;
        pins.Enqueue("1111");
        pins.Enqueue("2468");
        pins.Enqueue("2468");

        var result = await Sessions(terminal).ChangePinAsync();

        result.Succeeded.Should().BeTrue();
        result.Title.Should().Be("PIN changed");
        string.Join(' ', result.Lines.Append(result.Message)).Should().NotContain("2468").And.NotContain("1111");
    }

    [Fact]
    public async Task WhenTheHostIsDown_TheCustomerGetsASafeMessage()
    {
        await using var terminal = await TestTerminal.StartAsync();
        terminal.Services.GetRequiredService<SimulatedHostOptions>().Unreachable = true;

        var result = await Sessions(terminal).BalanceAsync();

        result.Succeeded.Should().BeFalse();
        result.Message.Should().NotBeNullOrEmpty().And.NotContain("Simulated");
    }
}
