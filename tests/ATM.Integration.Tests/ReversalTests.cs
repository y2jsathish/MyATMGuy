using ATM.Application.Audit;
using ATM.Application.Errors;
using ATM.Application.Jobs;
using ATM.Application.Persistence;
using ATM.Application.Transactions;
using ATM.Core.Entities;
using ATM.Core.Errors;
using ATM.Infrastructure.Devices.Simulator;
using Microsoft.Extensions.DependencyInjection;

namespace ATM.Integration.Tests;

/// <summary>Reversals the host did not accept are kept in the database and retried until the host takes them or the attempts run out.</summary>
public class ReversalTests
{
    private static async Task<TestTerminal> StartUnreachableAsync(Action<ReversalOptions>? configure = null)
    {
        var terminal = await TestTerminal.StartAsync();
        var options = terminal.Services.GetRequiredService<ReversalOptions>();
        options.FirstRetrySeconds = 0;
        configure?.Invoke(options);
        terminal.Services.GetRequiredService<SimulatedHostOptions>().Unreachable = true;
        return terminal;
    }

    private static async Task<List<string>> AuditActionsAsync(TestTerminal t)
    {
        await using var uow = t.Services.GetRequiredService<IUnitOfWorkFactory>().Create();
        return (await uow.Audit.PageAsync(1, 100)).Items.Select(a => a.Action).ToList();
    }

    private static IScheduledJob RetryJob(TestTerminal t) => t.Services.GetServices<IScheduledJob>().Single(j => j.Name == "reversal-retry");

    [Fact]
    public async Task UndeliveredReversal_IsStoredPending_AndAlertedOnce()
    {
        await using var terminal = await StartUnreachableAsync();

        var outcome = await terminal.Services.GetRequiredService<CashWithdrawalFlow>().RunAsync(100m);

        var pending = (await terminal.Services.GetRequiredService<ReversalService>().UnresolvedAsync()).Should().ContainSingle().Subject;
        pending.Id.Should().Be(outcome.TransactionId!.Value);
        pending.Amount.Should().Be(100m);
        pending.State.Should().Be(ReversalState.Pending);
        pending.Attempts.Should().Be(1);
        pending.LastError.Should().NotBeNullOrEmpty();
        terminal.Services.GetRequiredService<IErrorCounters>().Snapshot().Should().ContainKey(ErrorCatalogue.ReversalFailed.Code);
        (await AuditActionsAsync(terminal)).Should().Contain("ReversalFailed");
    }

    [Fact]
    public async Task RetryJob_DeliversTheReversal_OnceTheHostIsBack()
    {
        await using var terminal = await StartUnreachableAsync();
        var flow = terminal.Services.GetRequiredService<CashWithdrawalFlow>();
        var host = terminal.Services.GetRequiredService<SimulatedHostOptions>();
        var outcome = await flow.RunAsync(100m);
        host.Unreachable = false;

        await RetryJob(terminal).RunAsync(CancellationToken.None);

        terminal.Services.GetRequiredService<SimulatedHostGateway>().Reversals.Should().ContainSingle().Which.Amount.Should().Be(100m);
        (await terminal.Services.GetRequiredService<ReversalService>().UnresolvedAsync()).Should().BeEmpty();
        await using var uow = terminal.Services.GetRequiredService<IUnitOfWorkFactory>().Create();
        var stored = (await uow.Reversals.GetAsync(outcome.TransactionId!.Value))!;
        stored.State.Should().Be(ReversalState.Delivered);
        stored.Attempts.Should().Be(2);
        stored.ResolvedUtc.Should().NotBeNull();
        (await AuditActionsAsync(terminal)).Should().Contain("ReversalSent");
    }

    [Fact]
    public async Task RetryJob_KeepsRetrying_WhileTheHostStaysDown_ThenGivesUpAndRaisesTheAlertAgain()
    {
        await using var terminal = await StartUnreachableAsync(o => o.MaxAttempts = 3);
        await terminal.Services.GetRequiredService<CashWithdrawalFlow>().RunAsync(100m);
        var reversals = terminal.Services.GetRequiredService<ReversalService>();

        await RetryJob(terminal).RunAsync(CancellationToken.None);
        (await reversals.UnresolvedAsync()).Should().ContainSingle().Which.State.Should().Be(ReversalState.Pending);

        await RetryJob(terminal).RunAsync(CancellationToken.None);
        var given = (await reversals.UnresolvedAsync()).Should().ContainSingle().Subject;
        given.State.Should().Be(ReversalState.Abandoned);
        given.Attempts.Should().Be(3);
        (await AuditActionsAsync(terminal)).Should().Contain("ReversalAbandoned");

        await RetryJob(terminal).RunAsync(CancellationToken.None);
        (await reversals.UnresolvedAsync()).Single().Attempts.Should().Be(3, "an abandoned reversal is not sent again");
        terminal.Services.GetRequiredService<SimulatedHostGateway>().Reversals.Should().BeEmpty();
    }

    [Fact]
    public async Task RetryJob_LeavesAReversalAlone_UntilItsWaitIsOver()
    {
        await using var terminal = await StartUnreachableAsync(o => o.FirstRetrySeconds = 3600);
        await terminal.Services.GetRequiredService<CashWithdrawalFlow>().RunAsync(100m);
        terminal.Services.GetRequiredService<SimulatedHostOptions>().Unreachable = false;

        await RetryJob(terminal).RunAsync(CancellationToken.None);

        (await terminal.Services.GetRequiredService<ReversalService>().UnresolvedAsync()).Should().ContainSingle().Which.Attempts.Should().Be(1);
        terminal.Services.GetRequiredService<SimulatedHostGateway>().Reversals.Should().BeEmpty();
    }

    [Fact]
    public async Task ReversalThatIsDeliveredAtOnce_LeavesNothingToReconcile()
    {
        await using var terminal = await TestTerminal.StartAsync(simulator: s => s.CashDispenserStatus = ATM.Core.Devices.DeviceStatus.Fault);

        await terminal.Services.GetRequiredService<CashWithdrawalFlow>().RunAsync(100m);

        (await terminal.Services.GetRequiredService<ReversalService>().UnresolvedAsync()).Should().BeEmpty();
        await using var uow = terminal.Services.GetRequiredService<IUnitOfWorkFactory>().Create();
        (await uow.Reversals.DueAsync(DateTimeOffset.MaxValue, 10)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(6, 900)]
    [InlineData(30, 900)]
    public void RetryWait_DoublesEachAttempt_AndIsCapped(int attempts, int expectedSeconds)
    {
        new ReversalOptions().DelayAfter(attempts).Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }
}
