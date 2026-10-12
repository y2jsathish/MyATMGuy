using System.ComponentModel;
using ATM.Application;
using ATM.Application.Devices;
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
