using ATM.Application.Devices;
using ATM.Core.Devices;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace ATM.Application.Tests;

public class DeviceSelfTestServiceTests
{
    private static IDevicePort Port(DeviceKind kind, DeviceStatus status)
    {
        var port = Substitute.For<IDevicePort>();
        port.Kind.Returns(kind);
        port.GetHealthAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new DeviceHealth(kind, status, status.ToString())));
        return port;
    }

    [Fact]
    public async Task RunAsync_AllHealthy_ReportsOk()
    {
        var service = new DeviceSelfTestService([Port(DeviceKind.CardReader, DeviceStatus.Ok), Port(DeviceKind.PinPad, DeviceStatus.Ok)]);

        var report = await service.RunAsync();

        report.Devices.Should().HaveCount(2);
        report.Overall.Should().Be(DeviceStatus.Ok);
    }

    [Fact]
    public async Task RunAsync_OverallIsTheWorstStatus()
    {
        var service = new DeviceSelfTestService(
        [
            Port(DeviceKind.CardReader, DeviceStatus.Ok),
            Port(DeviceKind.CashDispenser, DeviceStatus.Degraded),
            Port(DeviceKind.ReceiptPrinter, DeviceStatus.Fault),
        ]);

        var report = await service.RunAsync();

        report.Overall.Should().Be(DeviceStatus.Fault);
    }

    [Fact]
    public async Task RunAsync_ThrowingDevice_IsReportedAsFault()
    {
        var broken = Substitute.For<IDevicePort>();
        broken.Kind.Returns(DeviceKind.PinPad);
        broken.GetHealthAsync(Arg.Any<CancellationToken>()).Returns<Task<DeviceHealth>>(_ => throw new InvalidOperationException("bus error"));
        var service = new DeviceSelfTestService([Port(DeviceKind.CardReader, DeviceStatus.Ok), broken]);

        var report = await service.RunAsync();

        report.Devices.Should().ContainSingle(d => d.Kind == DeviceKind.PinPad)
            .Which.Should().Match<DeviceHealth>(d => d.Status == DeviceStatus.Fault && d.Detail == "bus error");
    }

    [Fact]
    public async Task RunAsync_Cancellation_Propagates()
    {
        var cancelling = Substitute.For<IDevicePort>();
        cancelling.Kind.Returns(DeviceKind.CardReader);
        cancelling.GetHealthAsync(Arg.Any<CancellationToken>()).Returns<Task<DeviceHealth>>(_ => throw new OperationCanceledException());
        var service = new DeviceSelfTestService([cancelling]);

        var act = () => service.RunAsync();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RunAsync_NoDevices_ReportsOffline()
    {
        var report = await new DeviceSelfTestService([]).RunAsync();

        report.Overall.Should().Be(DeviceStatus.Offline);
    }

    [Fact]
    public async Task RunAsync_ReturnsDevicesInKindOrder()
    {
        var service = new DeviceSelfTestService([Port(DeviceKind.ReceiptPrinter, DeviceStatus.Ok), Port(DeviceKind.CardReader, DeviceStatus.Ok)]);

        var report = await service.RunAsync();

        report.Devices.Select(d => d.Kind).Should().ContainInOrder(DeviceKind.CardReader, DeviceKind.ReceiptPrinter);
    }
}

public class DependencyInjectionTests
{
    [Fact]
    public void AddApplication_RegistersServicesAndClock()
    {
        using var provider = new ServiceCollection()
            .AddSingleton(NSubstitute.Substitute.For<ATM.Application.Audit.IAuditStore>())
            .AddApplication()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        provider.GetRequiredService<IDeviceSelfTestService>().Should().NotBeNull();
        provider.GetRequiredService<TimeProvider>().Should().BeSameAs(TimeProvider.System);
    }
}
