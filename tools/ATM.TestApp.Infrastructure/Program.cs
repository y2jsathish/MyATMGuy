using ATM.Application;
using ATM.Application.Devices;
using ATM.Core.Devices;
using ATM.Core.Errors;
using ATM.Infrastructure;
using ATM.Infrastructure.Devices.Simulator;
using ATM.TestApp.Common;
using Microsoft.Extensions.DependencyInjection;

var runner = new CheckRunner("ATM.Infrastructure");

static ServiceProvider Build(Action<SimulatorOptions>? configure = null) =>
    new ServiceCollection()
        .AddApplication()
        .AddInfrastructure(configure)
        .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

await runner.CheckAsync("Container validates and resolves every device port", async () =>
{
    await using var provider = Build();

    Expect.True(provider.GetRequiredService<ICardReaderPort>() is not null, "Card reader port missing.");
    Expect.True(provider.GetRequiredService<IPinPadPort>() is not null, "Pin pad port missing.");
    Expect.True(provider.GetRequiredService<ICashDispenserPort>() is not null, "Cash dispenser port missing.");
    Expect.True(provider.GetRequiredService<IReceiptPrinterPort>() is not null, "Receipt printer port missing.");
    Expect.Equal(4, provider.GetServices<IDevicePort>().Count(), "Registered device ports");
});

await runner.CheckAsync("Self-test across the simulators is Ok", async () =>
{
    await using var provider = Build();

    var report = await provider.GetRequiredService<IDeviceSelfTestService>().RunAsync();

    Expect.Equal(DeviceStatus.Ok, report.Overall, "Overall status");
});

await runner.CheckAsync("Full withdrawal flow runs end to end", async () =>
{
    await using var provider = Build();
    var card = await provider.GetRequiredService<ICardReaderPort>().WaitForCardAsync(TimeSpan.FromSeconds(1));
    var pin = await provider.GetRequiredService<IPinPadPort>().ReadPinAsync(4, 6);
    var cash = await provider.GetRequiredService<ICashDispenserPort>().DispenseAsync(100m);
    var printer = provider.GetRequiredService<SimulatedReceiptPrinter>();
    await printer.PrintAsync($"Withdrawal 100 on {card!.Pan}");
    await provider.GetRequiredService<ICardReaderPort>().EjectAsync();

    Expect.True(pin.Completed, "PIN entry did not complete.");
    Expect.True(cash.Success, "Dispense failed.");
    Expect.True(printer.Printed.Single().Contains("411111******1111", StringComparison.Ordinal), "Receipt must carry the masked card number.");
    Expect.True(!printer.Printed.Single().Contains("4111111111111111", StringComparison.Ordinal), "Receipt leaked the full card number.");
});

await runner.CheckAsync("Injected dispenser fault is reported and thrown", async () =>
{
    await using var provider = Build(o => o.CashDispenserStatus = DeviceStatus.Fault);

    var report = await provider.GetRequiredService<IDeviceSelfTestService>().RunAsync();
    var error = await Expect.ThrowsAsync<AtmException>(() => provider.GetRequiredService<ICashDispenserPort>().DispenseAsync(20m));

    Expect.Equal(DeviceStatus.Fault, report.Overall, "Overall status");
    Expect.Equal("ATM-4001", error.Code.Code, "Error code");
});

await runner.CheckAsync("Insufficient cash fails cleanly and keeps the balance", async () =>
{
    var options = new SimulatorOptions { CashAvailable = 50m };
    var dispenser = new SimulatedCashDispenser(options);

    var result = await dispenser.DispenseAsync(100m);

    Expect.True(!result.Success, "Dispense should have failed.");
    Expect.Equal(50m, options.CashAvailable, "Cash available");
});

await runner.CheckAsync("Offline pin pad raises the offline error code", async () =>
{
    var pad = new SimulatedPinPad(new SimulatorOptions { PinPadStatus = DeviceStatus.Offline });

    var error = await Expect.ThrowsAsync<AtmException>(() => pad.ReadPinAsync(4, 6));

    Expect.Equal("ATM-4002", error.Code.Code, "Error code");
});

return runner.Complete(args);
