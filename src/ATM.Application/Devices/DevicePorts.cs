using ATM.Core.Devices;
using ATM.Core.ValueObjects;

namespace ATM.Application.Devices;

public sealed record DeviceHealth(DeviceKind Kind, DeviceStatus Status, string Detail);

public sealed record CardData(MaskedPan Pan);

/// <summary>PIN entry outcome. The clear PIN never leaves the pin pad; only an encrypted block does.</summary>
public sealed record PinEntryResult(bool Completed, int Length, ReadOnlyMemory<byte> EncryptedPinBlock);

public sealed record DispenseResult(bool Success, decimal Dispensed, string? Error);

/// <summary>
/// The only way Application code reaches hardware. Vendor adapters (XFS 3.x, XFS4IoT) implement these ports in Phase 2.
/// </summary>
public interface IDevicePort
{
    DeviceKind Kind { get; }

    Task<DeviceHealth> GetHealthAsync(CancellationToken cancellationToken = default);
}

public interface ICardReaderPort : IDevicePort
{
    /// <summary>Waits for a card. Returns null when the timeout expires first.</summary>
    Task<CardData?> WaitForCardAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    Task EjectAsync(CancellationToken cancellationToken = default);
}

public interface IPinPadPort : IDevicePort
{
    Task<PinEntryResult> ReadPinAsync(int minLength, int maxLength, CancellationToken cancellationToken = default);
}

public interface ICashDispenserPort : IDevicePort
{
    Task<DispenseResult> DispenseAsync(decimal amount, CancellationToken cancellationToken = default);
}

public interface IReceiptPrinterPort : IDevicePort
{
    /// <summary>Prints receipt text. Callers pass only masked card data.</summary>
    Task PrintAsync(string text, CancellationToken cancellationToken = default);
}
