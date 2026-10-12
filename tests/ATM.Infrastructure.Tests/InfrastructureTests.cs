using ATM.Application;
using ATM.Application.Devices;
using ATM.Core.Devices;
using ATM.Core.Errors;
using ATM.Infrastructure.Devices.Simulator;
using Microsoft.Extensions.DependencyInjection;

namespace ATM.Infrastructure.Tests;

public class SimulatedDeviceTests
{
    [Fact]
    public async Task CardReader_ReturnsMaskedCardOnly()
    {
        var reader = new SimulatedCardReader(new SimulatorOptions());

        var card = await reader.WaitForCardAsync(TimeSpan.FromSeconds(1));

        card.Should().NotBeNull();
        card!.Pan.Value.Should().Be("411111******1111");
        reader.CardInserted.Should().BeTrue();
    }

    [Fact]
    public async Task CardReader_EjectClearsTheCard()
    {
        var reader = new SimulatedCardReader(new SimulatorOptions());
        await reader.WaitForCardAsync(TimeSpan.FromSeconds(1));

        await reader.EjectAsync();

        reader.CardInserted.Should().BeFalse();
    }

    [Fact]
    public async Task PinPad_ReturnsAnEncryptedBlockNotThePin()
    {
        var pad = new SimulatedPinPad(new SimulatorOptions { PinLength = 4 });

        var result = await pad.ReadPinAsync(4, 6);

        result.Completed.Should().BeTrue();
        result.Length.Should().Be(4);
        result.EncryptedPinBlock.Length.Should().Be(8);
    }

    [Fact]
    public async Task PinPad_TooShortPin_IsNotCompleted()
    {
        var pad = new SimulatedPinPad(new SimulatorOptions { PinLength = 3 });

        var result = await pad.ReadPinAsync(4, 6);

        result.Completed.Should().BeFalse();
    }

    [Fact]
    public async Task Dispenser_DispensesAndReducesCash()
    {
        var options = new SimulatorOptions { CashAvailable = 300m };
        var dispenser = new SimulatedCashDispenser(options);

        var result = await dispenser.DispenseAsync(100m);

        result.Should().Be(new DispenseResult(true, 100m, null));
        options.CashAvailable.Should().Be(200m);
    }

    [Fact]
    public async Task Dispenser_InsufficientCash_FailsWithoutChangingBalance()
    {
        var options = new SimulatorOptions { CashAvailable = 50m };
        var dispenser = new SimulatedCashDispenser(options);

        var result = await dispenser.DispenseAsync(100m);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Insufficient");
        options.CashAvailable.Should().Be(50m);
    }

    [Fact]
    public async Task Dispenser_RejectsNonPositiveAmounts()
    {
        var dispenser = new SimulatedCashDispenser(new SimulatorOptions());

        var act = () => dispenser.DispenseAsync(0m);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(DeviceStatus.Fault, "ATM-4001")]
    [InlineData(DeviceStatus.Offline, "ATM-4002")]
    public async Task Dispenser_FaultOrOffline_ThrowsCatalogueError(DeviceStatus status, string expectedCode)
    {
        var dispenser = new SimulatedCashDispenser(new SimulatorOptions { CashDispenserStatus = status });

        var act = () => dispenser.DispenseAsync(20m);

        (await act.Should().ThrowAsync<AtmException>()).Which.Code.Code.Should().Be(expectedCode);
    }

    [Fact]
    public async Task Printer_RecordsPrintedText()
    {
        var printer = new SimulatedReceiptPrinter(new SimulatorOptions());

        await printer.PrintAsync("Card 411111******1111");

        printer.Printed.Should().ContainSingle().Which.Should().Contain("411111******1111");
    }

    [Fact]
    public async Task Health_ReflectsTheInjectedStatus()
    {
        var reader = new SimulatedCardReader(new SimulatorOptions { CardReaderStatus = DeviceStatus.Degraded });

        var health = await reader.GetHealthAsync();

        health.Should().Match<DeviceHealth>(h => h.Kind == DeviceKind.CardReader && h.Status == DeviceStatus.Degraded);
    }
}

public class CompositionTests
{
    private static ServiceProvider Build(Action<SimulatorOptions>? configure = null) =>
        new ServiceCollection()
            .AddApplication()
            .AddInfrastructure(configure)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

    [Fact]
    public async Task ContainerBuildsAndSelfTestSeesAllFourSimulators()
    {
        using var provider = Build();

        var report = await provider.GetRequiredService<IDeviceSelfTestService>().RunAsync();

        report.Devices.Select(d => d.Kind).Should().BeEquivalentTo(Enum.GetValues<DeviceKind>());
        report.Overall.Should().Be(DeviceStatus.Ok);
    }

    [Fact]
    public async Task InjectedFault_ShowsUpInTheSelfTest()
    {
        using var provider = Build(o => o.CashDispenserStatus = DeviceStatus.Fault);

        var report = await provider.GetRequiredService<IDeviceSelfTestService>().RunAsync();

        report.Overall.Should().Be(DeviceStatus.Fault);
    }

    [Fact]
    public void SpecificPortAndDevicePortShareOneInstance()
    {
        using var provider = Build();

        provider.GetRequiredService<ICashDispenserPort>()
            .Should().BeSameAs(provider.GetRequiredService<SimulatedCashDispenser>());
    }

    [Fact]
    public async Task FullWithdrawalFlow_WorksAgainstTheSimulators()
    {
        using var provider = Build();
        var card = await provider.GetRequiredService<ICardReaderPort>().WaitForCardAsync(TimeSpan.FromSeconds(1));
        var pin = await provider.GetRequiredService<IPinPadPort>().ReadPinAsync(4, 6);
        var cash = await provider.GetRequiredService<ICashDispenserPort>().DispenseAsync(100m);
        await provider.GetRequiredService<IReceiptPrinterPort>().PrintAsync($"Withdrawal 100 on {card!.Pan}");
        await provider.GetRequiredService<ICardReaderPort>().EjectAsync();

        pin.Completed.Should().BeTrue();
        cash.Success.Should().BeTrue();
        provider.GetRequiredService<SimulatedReceiptPrinter>().Printed.Single().Should().Contain("411111******1111");
    }
}
