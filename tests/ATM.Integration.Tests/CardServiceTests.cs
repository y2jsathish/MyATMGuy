using ATM.Application.Audit;
using ATM.Application.Persistence;
using ATM.Application.Transactions;
using ATM.Core.Devices;
using ATM.Core.Entities;
using ATM.Infrastructure.Devices.Simulator;
using Microsoft.Extensions.DependencyInjection;

namespace ATM.Integration.Tests;

/// <summary>Balance inquiry, mini statement and PIN change on the real platform with simulated devices and host.</summary>
public class CardServiceTests
{
    private static T Get<T>(TestTerminal t)
        where T : notnull => t.Services.GetRequiredService<T>();

    private static async Task<Transaction> StoredAsync(TestTerminal t, Guid id)
    {
        await using var uow = Get<IUnitOfWorkFactory>(t).Create();
        return (await uow.Transactions.GetAsync(id))!;
    }

    [Fact]
    public async Task Balance_ShowsHostBalance_PrintsMaskedReceipt_AndEjectsTheCard()
    {
        await using var terminal = await TestTerminal.StartAsync();

        var outcome = await Get<BalanceInquiryFlow>(terminal).RunAsync();

        outcome.Result.Should().Be(ServiceResult.Completed);
        outcome.Balance.Should().Be(1000m);
        outcome.ReceiptPrinted.Should().BeTrue();
        (await StoredAsync(terminal, outcome.TransactionId!.Value)).Status.Should().Be(TransactionStatus.Completed);
        Get<SimulatedCardReader>(terminal).CardInserted.Should().BeFalse();
        Get<SimulatedReceiptPrinter>(terminal).Printed.Should().ContainSingle().Subject.Should().Contain("411111******1111").And.NotContain("4111111111111111");
        (await Get<IAuditService>(terminal).VerifyAsync()).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Balance_Declined_StoresFailed_AndReportsTheHostCode()
    {
        await using var terminal = await TestTerminal.StartAsync();
        Get<SimulatedHostOptions>(terminal).DeclineAll = true;

        var outcome = await Get<BalanceInquiryFlow>(terminal).RunAsync();

        outcome.Result.Should().Be(ServiceResult.Declined);
        outcome.HostResponseCode.Should().Be("05");
        outcome.Balance.Should().BeNull();
        (await StoredAsync(terminal, outcome.TransactionId!.Value)).Status.Should().Be(TransactionStatus.Failed);
        Get<SimulatedCardReader>(terminal).CardInserted.Should().BeFalse();
    }

    [Fact]
    public async Task Balance_HostUnreachable_IsReportedAndCardIsReturned()
    {
        await using var terminal = await TestTerminal.StartAsync();
        Get<SimulatedHostOptions>(terminal).Unreachable = true;

        var outcome = await Get<BalanceInquiryFlow>(terminal).RunAsync();

        outcome.Result.Should().Be(ServiceResult.HostUnavailable);
        (await StoredAsync(terminal, outcome.TransactionId!.Value)).Status.Should().Be(TransactionStatus.Failed);
        Get<SimulatedCardReader>(terminal).CardInserted.Should().BeFalse();
    }

    [Fact]
    public async Task Balance_CardReaderFault_IsADeviceFault_WithNoTransaction()
    {
        await using var terminal = await TestTerminal.StartAsync();
        Get<SimulatorOptions>(terminal).CardReaderStatus = DeviceStatus.Fault;

        var outcome = await Get<BalanceInquiryFlow>(terminal).RunAsync();

        outcome.Result.Should().Be(ServiceResult.DeviceFault);
        outcome.TransactionId.Should().BeNull();
    }

    [Fact]
    public async Task Balance_PinNotCompleted_IsCancelled_BeforeTheHostIsAsked()
    {
        await using var terminal = await TestTerminal.StartAsync();
        Get<SimulatorOptions>(terminal).PinScript.Enqueue("12");

        var outcome = await Get<BalanceInquiryFlow>(terminal).RunAsync();

        outcome.Result.Should().Be(ServiceResult.PinCancelled);
        outcome.TransactionId.Should().BeNull();
        Get<SimulatedCardReader>(terminal).CardInserted.Should().BeFalse();
    }

    [Fact]
    public async Task MiniStatement_ReturnsAtMostTheConfiguredLines_AndPrintsThem()
    {
        await using var terminal = await TestTerminal.StartAsync();
        Get<TransactionOptions>(terminal).StatementLines = 2;

        var outcome = await Get<MiniStatementFlow>(terminal).RunAsync();

        outcome.Result.Should().Be(ServiceResult.Completed);
        outcome.Entries.Should().HaveCount(2);
        var receipt = Get<SimulatedReceiptPrinter>(terminal).Printed.Should().ContainSingle().Subject;
        receipt.Should().Contain("Salary").And.Contain("Groceries").And.NotContain("Utilities").And.NotContain("4111111111111111");
    }

    [Fact]
    public async Task MiniStatement_Declined_ReturnsNoEntries()
    {
        await using var terminal = await TestTerminal.StartAsync();
        Get<SimulatedHostOptions>(terminal).DeclineAll = true;

        var outcome = await Get<MiniStatementFlow>(terminal).RunAsync();

        outcome.Result.Should().Be(ServiceResult.Declined);
        outcome.Entries.Should().BeEmpty();
        Get<SimulatedReceiptPrinter>(terminal).Printed.Should().BeEmpty();
    }

    [Fact]
    public async Task PinChange_WithMatchingNewPins_IsAppliedByTheHost()
    {
        await using var terminal = await TestTerminal.StartAsync();
        foreach (var pin in new[] { "1234", "9876", "9876" })
        {
            Get<SimulatorOptions>(terminal).PinScript.Enqueue(pin);
        }

        var outcome = await Get<PinChangeFlow>(terminal).RunAsync();

        outcome.Result.Should().Be(ServiceResult.Completed);
        Get<SimulatedHostGateway>(terminal).PinChanges.Should().Be(1);
        (await StoredAsync(terminal, outcome.TransactionId!.Value)).Status.Should().Be(TransactionStatus.Completed);
        Get<SimulatedCardReader>(terminal).CardInserted.Should().BeFalse();
    }

    [Fact]
    public async Task PinChange_WhenConfirmationDiffers_IsRefused_AndHostNotCalled()
    {
        await using var terminal = await TestTerminal.StartAsync();
        foreach (var pin in new[] { "1234", "9876", "9877" })
        {
            Get<SimulatorOptions>(terminal).PinScript.Enqueue(pin);
        }

        var outcome = await Get<PinChangeFlow>(terminal).RunAsync();

        outcome.Result.Should().Be(ServiceResult.PinMismatch);
        Get<SimulatedHostGateway>(terminal).PinChanges.Should().Be(0);
        (await StoredAsync(terminal, outcome.TransactionId!.Value)).Status.Should().Be(TransactionStatus.Failed);
    }

    [Fact]
    public async Task PinChange_ToTheSamePin_IsRefused()
    {
        await using var terminal = await TestTerminal.StartAsync();
        foreach (var pin in new[] { "1234", "1234", "1234" })
        {
            Get<SimulatorOptions>(terminal).PinScript.Enqueue(pin);
        }

        var outcome = await Get<PinChangeFlow>(terminal).RunAsync();

        outcome.Result.Should().Be(ServiceResult.PinUnchanged);
        Get<SimulatedHostGateway>(terminal).PinChanges.Should().Be(0);
    }

    [Fact]
    public async Task PinChange_NewPinTooShort_IsCancelled()
    {
        await using var terminal = await TestTerminal.StartAsync();
        foreach (var pin in new[] { "1234", "98", "98" })
        {
            Get<SimulatorOptions>(terminal).PinScript.Enqueue(pin);
        }

        var outcome = await Get<PinChangeFlow>(terminal).RunAsync();

        outcome.Result.Should().Be(ServiceResult.PinCancelled);
        Get<SimulatedHostGateway>(terminal).PinChanges.Should().Be(0);
    }

    [Fact]
    public async Task PinChange_HostDeclines_IsReported()
    {
        await using var terminal = await TestTerminal.StartAsync();
        Get<SimulatedHostOptions>(terminal).DeclineAll = true;
        foreach (var pin in new[] { "1234", "9876", "9876" })
        {
            Get<SimulatorOptions>(terminal).PinScript.Enqueue(pin);
        }

        var outcome = await Get<PinChangeFlow>(terminal).RunAsync();

        outcome.Result.Should().Be(ServiceResult.Declined);
        outcome.HostResponseCode.Should().Be("05");
    }

    [Fact]
    public async Task PinChange_NeverPutsAPinInTheAuditTrail()
    {
        await using var terminal = await TestTerminal.StartAsync();
        foreach (var pin in new[] { "1234", "9876", "9876" })
        {
            Get<SimulatorOptions>(terminal).PinScript.Enqueue(pin);
        }

        await Get<PinChangeFlow>(terminal).RunAsync();

        await using var uow = Get<IUnitOfWorkFactory>(terminal).Create();
        var entries = (await uow.Audit.PageAsync(1, 100)).Items.Select(a => $"{a.Action} {a.Target} {a.Outcome}").ToList();
        entries.Should().NotBeEmpty();
        entries.Should().OnlyContain(e => !e.Contains("9876", StringComparison.Ordinal) && !e.Contains("1234", StringComparison.Ordinal));
    }
}
