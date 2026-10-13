using ATM.Application.Audit;
using ATM.Application.Errors;
using ATM.Application.Persistence;
using ATM.Application.Transactions;
using ATM.Core.Devices;
using ATM.Core.Entities;
using ATM.Core.Errors;
using ATM.Infrastructure.Devices.Simulator;
using Microsoft.Extensions.DependencyInjection;

namespace ATM.Integration.Tests;

/// <summary>The whole withdrawal path on the real platform: simulators for devices and host, real database, real audit chain.</summary>
public class WithdrawalTests
{
    private static CashWithdrawalFlow Flow(TestTerminal t) => t.Services.GetRequiredService<CashWithdrawalFlow>();

    private static SimulatedHostOptions Host(TestTerminal t) => t.Services.GetRequiredService<SimulatedHostOptions>();

    private static async Task<Transaction> StoredAsync(TestTerminal t, Guid id)
    {
        await using var uow = t.Services.GetRequiredService<IUnitOfWorkFactory>().Create();
        return (await uow.Transactions.GetAsync(id))!;
    }

    private static async Task<List<string>> AuditActionsAsync(TestTerminal t)
    {
        await using var uow = t.Services.GetRequiredService<IUnitOfWorkFactory>().Create();
        return (await uow.Audit.PageAsync(1, 100)).Items.Select(a => a.Action).ToList();
    }

    [Fact]
    public async Task Approved_DispensesCash_StoresCompleted_PrintsMaskedReceipt_AndEjectsTheCard()
    {
        await using var terminal = await TestTerminal.StartAsync();

        var outcome = await Flow(terminal).RunAsync(100m);

        outcome.Result.Should().Be(WithdrawalResult.Completed);
        outcome.Dispensed.Should().Be(100m);
        outcome.ReceiptPrinted.Should().BeTrue();
        (await StoredAsync(terminal, outcome.TransactionId!.Value)).Status.Should().Be(TransactionStatus.Completed);
        Host(terminal).Balance.Should().Be(900m);
        terminal.Services.GetRequiredService<SimulatedCardReader>().CardInserted.Should().BeFalse();
        var receipt = terminal.Services.GetRequiredService<SimulatedReceiptPrinter>().Printed.Should().ContainSingle().Subject;
        receipt.Should().Contain("411111******1111").And.NotContain("4111111111111111");
        (await AuditActionsAsync(terminal)).Should().Contain(["WithdrawalStarted", "Withdrawal"]);
        (await terminal.Services.GetRequiredService<IAuditService>().VerifyAsync()).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(1000)]
    public async Task InvalidAmount_IsRefusedBeforeAnyCardOrHostContact(int amount)
    {
        await using var terminal = await TestTerminal.StartAsync();

        var outcome = await Flow(terminal).RunAsync(amount);

        outcome.Result.Should().Be(WithdrawalResult.InvalidAmount);
        outcome.TransactionId.Should().BeNull();
        Host(terminal).Balance.Should().Be(1000m);
        await using var uow = terminal.Services.GetRequiredService<IUnitOfWorkFactory>().Create();
        (await uow.Transactions.PageAsync(1, 10)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Declined_FailsTheTransaction_WithoutCashOrReversal()
    {
        await using var terminal = await TestTerminal.StartAsync();
        Host(terminal).DeclineAll = true;

        var outcome = await Flow(terminal).RunAsync(50m);

        outcome.Result.Should().Be(WithdrawalResult.Declined);
        outcome.HostResponseCode.Should().Be("05");
        outcome.Dispensed.Should().Be(0m);
        var stored = await StoredAsync(terminal, outcome.TransactionId!.Value);
        stored.Status.Should().Be(TransactionStatus.Failed);
        stored.FailureReason.Should().Contain("05");
        terminal.Services.GetRequiredService<SimulatedHostGateway>().Reversals.Should().BeEmpty();
    }

    [Fact]
    public async Task InsufficientFunds_IsDeclinedWithCode51()
    {
        await using var terminal = await TestTerminal.StartAsync();
        Host(terminal).Balance = 20m;

        var outcome = await Flow(terminal).RunAsync(100m);

        outcome.Result.Should().Be(WithdrawalResult.Declined);
        outcome.HostResponseCode.Should().Be("51");
    }

    [Fact]
    public async Task DispenserFault_AfterApproval_SendsAFullReversal()
    {
        await using var terminal = await TestTerminal.StartAsync(simulator: s => s.CashDispenserStatus = DeviceStatus.Fault);

        var outcome = await Flow(terminal).RunAsync(100m);

        outcome.Result.Should().Be(WithdrawalResult.DispenseFailed);
        outcome.Dispensed.Should().Be(0m);
        (await StoredAsync(terminal, outcome.TransactionId!.Value)).Status.Should().Be(TransactionStatus.Failed);
        terminal.Services.GetRequiredService<SimulatedHostGateway>().Reversals.Should().ContainSingle().Which.Amount.Should().Be(100m);
        Host(terminal).Balance.Should().Be(1000m, "the reversal gives the money back");
        terminal.Services.GetRequiredService<IErrorCounters>().Snapshot().Should().ContainKey(ErrorCatalogue.DispenseFailed.Code);
        (await AuditActionsAsync(terminal)).Should().Contain("ReversalSent");
        terminal.Services.GetRequiredService<SimulatedCardReader>().CardInserted.Should().BeFalse();
    }

    [Fact]
    public async Task HostUnreachable_FailsTheTransaction_AndAnUndeliverableReversalIsAuditedAsCritical()
    {
        await using var terminal = await TestTerminal.StartAsync();
        Host(terminal).Unreachable = true;

        var outcome = await Flow(terminal).RunAsync(100m);

        outcome.Result.Should().Be(WithdrawalResult.HostUnavailable);
        (await StoredAsync(terminal, outcome.TransactionId!.Value)).Status.Should().Be(TransactionStatus.Failed);
        var counters = terminal.Services.GetRequiredService<IErrorCounters>().Snapshot();
        counters.Should().ContainKey(ErrorCatalogue.HostUnavailable.Code).And.ContainKey(ErrorCatalogue.ReversalFailed.Code);
        (await AuditActionsAsync(terminal)).Should().Contain("ReversalFailed");
    }

    [Fact]
    public async Task PinNotCompleted_EndsTheSession_AndEjectsTheCard()
    {
        await using var terminal = await TestTerminal.StartAsync(simulator: s => s.PinLength = 2);

        var outcome = await Flow(terminal).RunAsync(100m);

        outcome.Result.Should().Be(WithdrawalResult.PinCancelled);
        outcome.TransactionId.Should().BeNull();
        terminal.Services.GetRequiredService<SimulatedCardReader>().CardInserted.Should().BeFalse();
        Host(terminal).Balance.Should().Be(1000m);
    }

    [Fact]
    public async Task PrinterFault_DoesNotUndoTheWithdrawal()
    {
        await using var terminal = await TestTerminal.StartAsync(simulator: s => s.ReceiptPrinterStatus = DeviceStatus.Fault);

        var outcome = await Flow(terminal).RunAsync(100m);

        outcome.Result.Should().Be(WithdrawalResult.Completed);
        outcome.ReceiptPrinted.Should().BeFalse();
        (await StoredAsync(terminal, outcome.TransactionId!.Value)).Status.Should().Be(TransactionStatus.Completed);
    }

    [Fact]
    public async Task CardReaderFault_ReturnsADeviceFault_AndNothingIsStored()
    {
        await using var terminal = await TestTerminal.StartAsync(simulator: s => s.CardReaderStatus = DeviceStatus.Fault);

        var outcome = await Flow(terminal).RunAsync(100m);

        outcome.Result.Should().Be(WithdrawalResult.DeviceFault);
        outcome.Message.Should().NotContain("Fault", "the customer never sees device internals");
        await using var uow = terminal.Services.GetRequiredService<IUnitOfWorkFactory>().Create();
        (await uow.Transactions.PageAsync(1, 10)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_StillEjectsTheCard()
    {
        await using var terminal = await TestTerminal.StartAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => Flow(terminal).RunAsync(100m, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        terminal.Services.GetRequiredService<SimulatedCardReader>().CardInserted.Should().BeFalse();
    }

    [Fact]
    public async Task ManyWithdrawals_KeepTheAuditChainValid_AndTheBalanceExact()
    {
        await using var terminal = await TestTerminal.StartAsync();

        for (var i = 0; i < 25; i++)
        {
            (await Flow(terminal).RunAsync(20m)).Result.Should().Be(WithdrawalResult.Completed);
        }

        Host(terminal).Balance.Should().Be(500m);
        (await terminal.Services.GetRequiredService<IAuditService>().VerifyAsync()).IsValid.Should().BeTrue();
        await using var uow = terminal.Services.GetRequiredService<IUnitOfWorkFactory>().Create();
        (await uow.Transactions.PageAsync(1, 100)).Items.Should().HaveCount(25).And.OnlyContain(t => t.Status == TransactionStatus.Completed);
    }
}
