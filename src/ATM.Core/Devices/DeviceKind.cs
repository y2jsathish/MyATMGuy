namespace ATM.Core.Devices;

public enum DeviceKind
{
    CardReader,
    PinPad,
    CashDispenser,
    ReceiptPrinter,
}

/// <summary>Device health, ordered from best to worst so the maximum is the overall state.</summary>
public enum DeviceStatus
{
    Ok,
    Degraded,
    Fault,
    Offline,
}
