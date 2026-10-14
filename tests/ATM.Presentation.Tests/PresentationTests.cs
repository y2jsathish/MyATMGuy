using System.ComponentModel;
using ATM.Application;
using ATM.Application.Devices;
using ATM.Application.Health;
using ATM.Application.Notifications;
using ATM.Application.Supervisor;
using ATM.Application.Transactions;
using ATM.Core.Devices;
using ATM.Infrastructure;
using ATM.Presentation.Navigation;
using ATM.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace ATM.Presentation.Tests;

public class NavigationTests
{
    private static ServiceProvider Build() =>
        new ServiceCollection()
            .AddApplication()
            .AddInfrastructure()
            .AddPresentation()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

    [Fact]
    public void NavigateTo_SetsTheCurrentViewModel()
    {
        using var provider = Build();
        var navigation = provider.GetRequiredService<INavigationService>();

        navigation.NavigateTo<HomeViewModel>();

        navigation.Current.Should().BeOfType<HomeViewModel>();
    }

    [Fact]
    public void NavigateTo_RaisesPropertyChanged()
    {
        using var provider = Build();
        var navigation = provider.GetRequiredService<INavigationService>();
        var changed = new List<string?>();
        navigation.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        navigation.NavigateTo<MaintenanceViewModel>();

        changed.Should().Contain(nameof(INavigationService.Current));
    }

    [Fact]
    public void NavigateTo_DisposesThePreviousViewModel()
    {
        var disposable = new DisposableViewModel();
        var services = new ServiceCollection()
            .AddTransient(_ => disposable)
            .AddTransient<HomeViewModel>()
            .BuildServiceProvider();
        var navigation = new NavigationService(services);

        navigation.NavigateTo<DisposableViewModel>();
        navigation.NavigateTo<HomeViewModel>();

        disposable.Disposed.Should().BeTrue();
    }

    [Fact]
    public void Shell_ForwardsCurrentChangesAndExposesCommands()
    {
        using var provider = Build();
        var shell = provider.GetRequiredService<ShellViewModel>();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)shell).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        shell.ShowMaintenanceCommand.Execute(null);

        shell.Current.Should().BeOfType<MaintenanceViewModel>();
        raised.Should().Contain(nameof(ShellViewModel.Current));

        shell.ShowHomeCommand.Execute(null);

        shell.Current.Should().BeOfType<HomeViewModel>();
    }

    private sealed class DisposableViewModel : ViewModelBase, IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}

public class MaintenanceViewModelTests
{
    [Fact]
    public async Task RunSelfTest_PopulatesDevicesAndOverall()
    {
        var service = Substitute.For<IDeviceSelfTestService>();
        service.RunAsync(Arg.Any<CancellationToken>()).Returns(new DeviceSelfTestReport(
        [
            new DeviceHealth(DeviceKind.CardReader, DeviceStatus.Ok, "ready"),
            new DeviceHealth(DeviceKind.CashDispenser, DeviceStatus.Degraded, "low cash"),
        ]));
        var viewModel = new MaintenanceViewModel(service);

        await viewModel.RunSelfTestCommand.ExecuteAsync(null);

        viewModel.Devices.Should().HaveCount(2);
        viewModel.Devices[1].Should().Be(new DeviceHealthItem("CashDispenser", "Degraded", "low cash"));
        viewModel.Overall.Should().Be("Degraded");
        viewModel.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task RunSelfTest_ReplacesPreviousResults()
    {
        var service = Substitute.For<IDeviceSelfTestService>();
        service.RunAsync(Arg.Any<CancellationToken>()).Returns(new DeviceSelfTestReport([new DeviceHealth(DeviceKind.PinPad, DeviceStatus.Ok, "ok")]));
        var viewModel = new MaintenanceViewModel(service);

        await viewModel.RunSelfTestCommand.ExecuteAsync(null);
        await viewModel.RunSelfTestCommand.ExecuteAsync(null);

        viewModel.Devices.Should().HaveCount(1);
    }

    [Fact]
    public void BeforeRunning_OverallIsNotRun()
    {
        new MaintenanceViewModel(Substitute.For<IDeviceSelfTestService>()).Overall.Should().Be("Not run");
    }

    [Fact]
    public void HomeViewModel_ShowsTheWelcomePrompt()
    {
        var home = new HomeViewModel();

        home.Heading.Should().Be("Welcome");
        home.Prompt.Should().Contain("card");
    }
}

public class SupervisorHealthViewModelTests
{
    private sealed class OneCheck(HealthCheckResult result) : IHealthCheck
    {
        public string Name => "Disk";

        public Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private static (MaintenanceViewModel ViewModel, List<(string Action, string Outcome)> Audited) Create(string passcodeHash, HealthCheckResult? check = null, IReversalReport? reversals = null)
    {
        var audited = new List<(string, string)>();
        var audit = Substitute.For<ATM.Application.Audit.IAuditService>();
        audit.RecordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ATM.Core.Entities.AuditCategory>(), Arg.Any<ATM.Core.ValueObjects.CorrelationId?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                audited.Add((call.ArgAt<string>(1), call.ArgAt<string>(3)));
                return Task.FromResult<ATM.Core.Entities.AuditEntry>(null!);
            });
        var bus = new ATM.Application.Events.InProcessEventBus(new ATM.Application.Events.InMemoryDeadLetterStore(), new ATM.Application.Errors.ErrorCounters(), TimeProvider.System);
        var monitor = new HealthMonitor([new OneCheck(check ?? HealthCheckResult.Healthy("120 GB free"))], bus, TimeProvider.System);
        var session = new SupervisorSession(new SupervisorOptions { PasscodeHash = passcodeHash }, TimeProvider.System);
        return (new MaintenanceViewModel(Substitute.For<IDeviceSelfTestService>(), session, monitor, audit, reversals), audited);
    }

    [Fact]
    public async Task Supervisor_SeesReversalsWaitingForTheHost_ButOnlyAfterLogin()
    {
        var now = DateTimeOffset.UtcNow;
        var waiting = ATM.Core.Entities.ReversalRequest.Create(Guid.NewGuid(), ATM.Core.ValueObjects.TerminalId.Create("ATM-0777"), 100m, now, now);
        waiting.MarkFailed("host timed out", now, now.AddMinutes(1));
        var report = Substitute.For<IReversalReport>();
        report.UnresolvedAsync(Arg.Any<CancellationToken>()).Returns([waiting]);
        var (vm, _) = Create(PasscodeHasher.Hash("2468"), reversals: report);

        await vm.RefreshHealthCommand.ExecuteAsync(null);
        vm.Reversals.Should().BeEmpty();

        vm.Passcode = "2468";
        await vm.EnterSupervisorCommand.ExecuteAsync(null);

        var item = vm.Reversals.Should().ContainSingle().Subject;
        item.Amount.Should().Be("100.00");
        item.State.Should().Be("Pending");
        item.Attempts.Should().Be(1);
        item.LastError.Should().Be("host timed out");

        vm.ExitSupervisorCommand.Execute(null);
        vm.Reversals.Should().BeEmpty();
        vm.Dispose();
    }

    [Fact]
    public async Task HealthList_IsHiddenAndEmpty_UntilSupervisorLogsIn()
    {
        var (vm, _) = Create(PasscodeHasher.Hash("2468"));

        await vm.RefreshHealthCommand.ExecuteAsync(null);

        vm.IsSupervisor.Should().BeFalse();
        vm.Health.Should().BeEmpty();
    }

    [Fact]
    public async Task CorrectPasscode_ShowsHealth_AndAuditsTheLogin()
    {
        var (vm, audited) = Create(PasscodeHasher.Hash("2468"), HealthCheckResult.Degraded("low disk"));
        vm.Passcode = "2468";

        await vm.EnterSupervisorCommand.ExecuteAsync(null);

        vm.IsSupervisor.Should().BeTrue();
        vm.Passcode.Should().BeEmpty("the passcode must not linger");
        vm.HealthOverall.Should().Be("Degraded");
        vm.Health.Should().ContainSingle().Which.Should().Match<HealthItem>(h => h.Name == "Disk" && h.Message == "low disk");
        audited.Should().Contain(("SupervisorLogin", "Granted"));
        vm.Dispose();
    }

    [Fact]
    public async Task WrongPasscode_IsDenied_AndAudited()
    {
        var (vm, audited) = Create(PasscodeHasher.Hash("2468"));
        vm.Passcode = "0000";

        await vm.EnterSupervisorCommand.ExecuteAsync(null);

        vm.IsSupervisor.Should().BeFalse();
        vm.SupervisorMessage.Should().Contain("Wrong");
        audited.Should().Contain(("SupervisorLogin", "Denied"));
    }

    [Fact]
    public async Task Exit_ClearsHealth()
    {
        var (vm, _) = Create(PasscodeHasher.Hash("2468"));
        vm.Passcode = "2468";
        await vm.EnterSupervisorCommand.ExecuteAsync(null);

        vm.ExitSupervisorCommand.Execute(null);

        vm.IsSupervisor.Should().BeFalse();
        vm.Health.Should().BeEmpty();
    }

    [Fact]
    public void WithoutSupervisorService_ModeIsUnavailable() =>
        new MaintenanceViewModel(Substitute.For<IDeviceSelfTestService>()).CanUseSupervisorMode.Should().BeFalse();

    [Fact]
    public void Banner_ShowsNewestNotification()
    {
        SynchronizationContext.SetSynchronizationContext(null); // the test runner's context would defer the update
        var channel = new UiBannerChannel();
        var shell = new ShellViewModel(Substitute.For<INavigationService>(), channel);

        channel.SendAsync(new Notification(ATM.Core.Errors.ErrorSeverity.High, "Health: Disk is Unhealthy", "2 MB free", DateTimeOffset.UtcNow));

        shell.Banner.Should().Be("Health: Disk is Unhealthy: 2 MB free");
    }
}

public class CustomerServiceViewModelTests
{
    private static ICustomerSessionService Sessions(Func<CustomerSessionResult>? result = null)
    {
        var sessions = Substitute.For<ICustomerSessionService>();
        sessions.QuickAmounts.Returns([20m, 50m, 100m]);
        var answer = result ?? (() => new CustomerSessionResult(true, "Please take your cash", "Done.", ["Amount 100.00", "Please take your card."]));
        sessions.WithdrawAsync(Arg.Any<decimal>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(answer()));
        sessions.BalanceAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(answer()));
        sessions.MiniStatementAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(answer()));
        sessions.ChangePinAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(answer()));
        return sessions;
    }

    [Fact]
    public void StartsOnTheMenu_WithTheQuickAmounts()
    {
        var vm = new CustomerServiceViewModel(Sessions());

        vm.Stage.Should().Be(CustomerStage.Choose);
        vm.IsChoosing.Should().BeTrue();
        vm.QuickAmounts.Should().Equal(20m, 50m, 100m);
        vm.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task Withdraw_ShowsTheResultWithItsLines_ThenDoneReturnsToTheMenu()
    {
        var sessions = Sessions();
        var vm = new CustomerServiceViewModel(sessions, TimeSpan.FromMinutes(5));

        await vm.WithdrawCommand.ExecuteAsync(100m);

        await sessions.Received(1).WithdrawAsync(100m, Arg.Any<CancellationToken>());
        vm.IsResult.Should().BeTrue();
        vm.IsSuccess.Should().BeTrue();
        vm.ResultTitle.Should().Be("Please take your cash");
        vm.ResultLines.Should().Equal("Amount 100.00", "Please take your card.");

        vm.DoneCommand.Execute(null);

        vm.IsChoosing.Should().BeTrue();
        vm.ResultLines.Should().BeEmpty("the next customer must not see the last result");
        vm.ResultTitle.Should().BeEmpty();
        vm.Dispose();
    }

    [Fact]
    public async Task WhileTheServiceRuns_TheScreenShowsThePrompt_AndASecondStartIsIgnored()
    {
        var release = new TaskCompletionSource<CustomerSessionResult>();
        var sessions = Substitute.For<ICustomerSessionService>();
        sessions.BalanceAsync(Arg.Any<CancellationToken>()).Returns(_ => release.Task);
        var vm = new CustomerServiceViewModel(sessions, TimeSpan.FromMinutes(5));

        var running = vm.BalanceCommand.ExecuteAsync(null);

        vm.IsWorking.Should().BeTrue();
        vm.Prompt.Should().Contain("Insert your card");
        await vm.BalanceCommand.ExecuteAsync(null);
        await sessions.Received(1).BalanceAsync(Arg.Any<CancellationToken>());

        release.SetResult(new CustomerSessionResult(true, "Your balance", "ok", ["Available 900.00"]));
        await running;
        vm.IsResult.Should().BeTrue();
        vm.Dispose();
    }

    [Fact]
    public async Task AFailedService_IsShownAsNotCompleted()
    {
        var vm = new CustomerServiceViewModel(Sessions(() => new CustomerSessionResult(false, "Transaction not completed", "Your bank declined this request.", [])), TimeSpan.FromMinutes(5));

        await vm.MiniStatementCommand.ExecuteAsync(null);

        vm.IsSuccess.Should().BeFalse();
        vm.ResultMessage.Should().Be("Your bank declined this request.");
        vm.Dispose();
    }

    [Fact]
    public async Task AnUnexpectedError_ShowsAGenericMessage_WithoutInternals()
    {
        var sessions = Substitute.For<ICustomerSessionService>();
        sessions.ChangePinAsync(Arg.Any<CancellationToken>()).Returns<Task<CustomerSessionResult>>(_ => throw new InvalidOperationException("db password is hunter2"));
        var vm = new CustomerServiceViewModel(sessions, TimeSpan.FromMinutes(5));

        await vm.ChangePinCommand.ExecuteAsync(null);

        vm.IsResult.Should().BeTrue();
        vm.IsSuccess.Should().BeFalse();
        vm.ResultMessage.Should().NotContain("hunter2").And.Be("This ATM cannot complete your request.");
        vm.Dispose();
    }

    [Fact]
    public async Task Cancel_StopsTheService_AndReturnsToTheMenu()
    {
        var sessions = Substitute.For<ICustomerSessionService>();
        sessions.BalanceAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
            return new CustomerSessionResult(true, "never", "never", []);
        });
        var vm = new CustomerServiceViewModel(sessions, TimeSpan.FromMinutes(5));

        var running = vm.BalanceCommand.ExecuteAsync(null);
        vm.CancelCommand.Execute(null);
        await running;

        vm.IsChoosing.Should().BeTrue();
        vm.Dispose();
    }

    [Fact]
    public async Task TheResult_GoesAwayByItself_AfterTheTimeout()
    {
        var vm = new CustomerServiceViewModel(Sessions(), TimeSpan.FromMilliseconds(50));

        await vm.BalanceCommand.ExecuteAsync(null);
        vm.IsResult.Should().BeTrue();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!vm.IsChoosing && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        vm.IsChoosing.Should().BeTrue();
        vm.ResultLines.Should().BeEmpty();
        vm.Dispose();
    }

    [Fact]
    public async Task WithoutASessionService_NothingStarts()
    {
        var vm = new CustomerServiceViewModel();

        await vm.BalanceCommand.ExecuteAsync(null);

        vm.IsAvailable.Should().BeFalse();
        vm.IsChoosing.Should().BeTrue();
        vm.QuickAmounts.Should().BeEmpty();
    }

    [Fact]
    public void Shell_CanShowTheCustomerServices()
    {
        using var provider = new ServiceCollection().AddApplication().AddInfrastructure().AddPresentation().BuildServiceProvider();
        var shell = provider.GetRequiredService<ShellViewModel>();

        shell.ShowServicesCommand.Execute(null);

        shell.Current.Should().BeOfType<CustomerServiceViewModel>();
    }
}
