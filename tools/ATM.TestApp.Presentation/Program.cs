using ATM.Application;
using ATM.Core.Devices;
using ATM.Infrastructure;
using ATM.Presentation;
using ATM.Presentation.Navigation;
using ATM.Presentation.ViewModels;
using ATM.TestApp.Common;
using Microsoft.Extensions.DependencyInjection;

var runner = new CheckRunner("ATM.Presentation (ViewModels)");

static ServiceProvider Build(Action<ATM.Infrastructure.Devices.Simulator.SimulatorOptions>? configure = null) =>
    new ServiceCollection()
        .AddApplication()
        .AddInfrastructure(configure)
        .AddPresentation()
        .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

runner.Check("Container validates with all ViewModels registered", () =>
{
    using var provider = Build();

    Expect.True(provider.GetRequiredService<ShellViewModel>() is not null, "ShellViewModel missing.");
    Expect.True(provider.GetRequiredService<HomeViewModel>() is not null, "HomeViewModel missing.");
    Expect.True(provider.GetRequiredService<MaintenanceViewModel>() is not null, "MaintenanceViewModel missing.");
});

runner.Check("Navigation switches the current ViewModel", () =>
{
    using var provider = Build();
    var navigation = provider.GetRequiredService<INavigationService>();

    navigation.NavigateTo<HomeViewModel>();
    Expect.True(navigation.Current is HomeViewModel, "Expected HomeViewModel.");

    navigation.NavigateTo<MaintenanceViewModel>();
    Expect.True(navigation.Current is MaintenanceViewModel, "Expected MaintenanceViewModel.");
});

runner.Check("Shell commands drive navigation", () =>
{
    using var provider = Build();
    var shell = provider.GetRequiredService<ShellViewModel>();

    shell.ShowMaintenanceCommand.Execute(null);
    Expect.True(shell.Current is MaintenanceViewModel, "Maintenance command did not navigate.");

    shell.ShowHomeCommand.Execute(null);
    Expect.True(shell.Current is HomeViewModel, "Home command did not navigate.");
});

await runner.CheckAsync("Maintenance self-test fills the device list from the simulators", async () =>
{
    using var provider = Build();
    var viewModel = provider.GetRequiredService<MaintenanceViewModel>();

    await viewModel.RunSelfTestCommand.ExecuteAsync(null);

    Expect.Equal(4, viewModel.Devices.Count, "Device rows");
    Expect.Equal("Ok", viewModel.Overall, "Overall");
});

await runner.CheckAsync("Maintenance self-test shows an injected fault", async () =>
{
    using var provider = Build(o => o.ReceiptPrinterStatus = DeviceStatus.Fault);
    var viewModel = provider.GetRequiredService<MaintenanceViewModel>();

    await viewModel.RunSelfTestCommand.ExecuteAsync(null);

    Expect.Equal("Fault", viewModel.Overall, "Overall");
});

return runner.Complete(args);
