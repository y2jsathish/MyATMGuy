using ATM.Core.Devices;

namespace ATM.Application.Devices;

public sealed record DeviceSelfTestReport(IReadOnlyList<DeviceHealth> Devices)
{
    /// <summary>The worst status across all devices, or Offline when no device is registered.</summary>
    public DeviceStatus Overall => Devices.Count == 0 ? DeviceStatus.Offline : Devices.Max(d => d.Status);
}

public interface IDeviceSelfTestService
{
    Task<DeviceSelfTestReport> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>Asks every registered device for its health. A device that throws is reported as a fault, never as a crash.</summary>
public sealed class DeviceSelfTestService(IEnumerable<IDevicePort> ports) : IDeviceSelfTestService
{
    public async Task<DeviceSelfTestReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<DeviceHealth>();
        foreach (var port in ports.OrderBy(p => p.Kind))
        {
            try
            {
                results.Add(await port.GetHealthAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(new DeviceHealth(port.Kind, DeviceStatus.Fault, ex.Message));
            }
        }

        return new DeviceSelfTestReport(results);
    }
}
