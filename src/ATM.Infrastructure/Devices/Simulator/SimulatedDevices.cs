using System.Security.Cryptography;
using ATM.Application.Devices;
using ATM.Core.Devices;
using ATM.Core.Errors;
using ATM.Core.ValueObjects;

namespace ATM.Infrastructure.Devices.Simulator;

/// <summary>
/// Scenario switches for the simulated hardware. Properties are mutable so tests and the portable test apps can inject faults at runtime.
/// </summary>
public sealed class SimulatorOptions
{
    public DeviceStatus CardReaderStatus { get; set; } = DeviceStatus.Ok;

    public DeviceStatus PinPadStatus { get; set; } = DeviceStatus.Ok;

    public DeviceStatus CashDispenserStatus { get; set; } = DeviceStatus.Ok;

    public DeviceStatus ReceiptPrinterStatus { get; set; } = DeviceStatus.Ok;

    /// <summary>A well-known test card number that passes the Luhn check. Never a real card.</summary>
    public string CardPan { get; set; } = "4111111111111111";

    public int PinLength { get; set; } = 4;

    /// <summary>PINs the simulated customer types, in order, one per PIN entry. When empty, a default PIN of <see cref="PinLength"/> ones is used.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> PinScript { get; } = new();

    public decimal CashAvailable { get; set; } = 5000m;
}

internal static class SimulatorGuard
{
    public static DeviceHealth Health(DeviceKind kind, DeviceStatus status) =>
        new(kind, status, status switch
        {
            DeviceStatus.Ok => "Simulator ready",
            DeviceStatus.Degraded => "Simulator degraded",
            DeviceStatus.Fault => "Simulator fault injected",
            _ => "Simulator offline",
        });

    public static void EnsureUsable(DeviceKind kind, DeviceStatus status)
    {
        switch (status)
        {
            case DeviceStatus.Offline:
                throw new AtmException(ErrorCatalogue.DeviceOffline, $"{kind} is offline.");
            case DeviceStatus.Fault:
                throw new AtmException(ErrorCatalogue.DeviceFault, $"{kind} reported a fault.");
        }
    }
}

public sealed class SimulatedCardReader(SimulatorOptions options) : ICardReaderPort
{
    public DeviceKind Kind => DeviceKind.CardReader;

    public bool CardInserted { get; private set; }

    public Task<DeviceHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(SimulatorGuard.Health(Kind, options.CardReaderStatus));

    public Task<CardData?> WaitForCardAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SimulatorGuard.EnsureUsable(Kind, options.CardReaderStatus);
        CardInserted = true;
        return Task.FromResult<CardData?>(new CardData(MaskedPan.FromPan(options.CardPan)));
    }

    public Task EjectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SimulatorGuard.EnsureUsable(Kind, options.CardReaderStatus);
        CardInserted = false;
        return Task.CompletedTask;
    }
}

public sealed class SimulatedPinPad(SimulatorOptions options) : IPinPadPort
{
    public DeviceKind Kind => DeviceKind.PinPad;

    public Task<DeviceHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(SimulatorGuard.Health(Kind, options.PinPadStatus));

    public Task<PinEntryResult> ReadPinAsync(int minLength, int maxLength, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SimulatorGuard.EnsureUsable(Kind, options.PinPadStatus);

        var typed = options.PinScript.TryDequeue(out var scripted) ? scripted : new string('1', options.PinLength);
        var length = typed.Length;
        if (length < minLength || length > maxLength)
        {
            return Task.FromResult(new PinEntryResult(false, length, ReadOnlyMemory<byte>.Empty));
        }

        // A random block stands in for the encrypted PIN block a real pin pad would return.
        return Task.FromResult(new PinEntryResult(true, length, RandomNumberGenerator.GetBytes(8), SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(typed))));
    }
}

public sealed class SimulatedCashDispenser(SimulatorOptions options) : ICashDispenserPort
{
    private readonly object _gate = new();

    public DeviceKind Kind => DeviceKind.CashDispenser;

    public Task<DeviceHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(SimulatorGuard.Health(Kind, options.CashDispenserStatus));

    public Task<DispenseResult> DispenseAsync(decimal amount, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        SimulatorGuard.EnsureUsable(Kind, options.CashDispenserStatus);

        lock (_gate)
        {
            if (amount > options.CashAvailable)
            {
                return Task.FromResult(new DispenseResult(false, 0m, "Insufficient cash in dispenser."));
            }

            options.CashAvailable -= amount;
            return Task.FromResult(new DispenseResult(true, amount, null));
        }
    }
}

public sealed class SimulatedReceiptPrinter(SimulatorOptions options) : IReceiptPrinterPort
{
    private readonly List<string> _printed = [];
    private readonly object _gate = new();

    public DeviceKind Kind => DeviceKind.ReceiptPrinter;

    /// <summary>Everything printed so far, for tests and the portable test apps.</summary>
    public IReadOnlyList<string> Printed
    {
        get
        {
            lock (_gate)
            {
                return _printed.ToArray();
            }
        }
    }

    public Task<DeviceHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(SimulatorGuard.Health(Kind, options.ReceiptPrinterStatus));

    public Task PrintAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(text);
        SimulatorGuard.EnsureUsable(Kind, options.ReceiptPrinterStatus);

        lock (_gate)
        {
            _printed.Add(text);
        }

        return Task.CompletedTask;
    }
}
