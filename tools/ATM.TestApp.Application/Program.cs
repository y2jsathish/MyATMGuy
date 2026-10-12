using System.Security.Cryptography;
using ATM.Application.Configuration;
using ATM.Application.Correlation;
using ATM.Application.Devices;
using ATM.Application.Masking;
using ATM.Core.Devices;
using ATM.TestApp.Common;

var runner = new CheckRunner("ATM.Application");

await runner.CheckAsync("Self-test reports the worst device status", async () =>
{
    var service = new DeviceSelfTestService(
    [
        new StubPort(DeviceKind.CardReader, DeviceStatus.Ok),
        new StubPort(DeviceKind.CashDispenser, DeviceStatus.Degraded),
        new StubPort(DeviceKind.ReceiptPrinter, DeviceStatus.Fault),
    ]);

    var report = await service.RunAsync();

    Expect.Equal(3, report.Devices.Count, "Device count");
    Expect.Equal(DeviceStatus.Fault, report.Overall, "Overall status");
});

await runner.CheckAsync("A throwing device becomes a fault, not a crash", async () =>
{
    var service = new DeviceSelfTestService([new StubPort(DeviceKind.PinPad, DeviceStatus.Ok, throws: true)]);

    var report = await service.RunAsync();

    Expect.Equal(DeviceStatus.Fault, report.Devices.Single().Status, "Status");
    Expect.Equal("bus error", report.Devices.Single().Detail, "Detail");
});

await runner.CheckAsync("Cancellation is not swallowed", async () =>
{
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();
    var service = new DeviceSelfTestService([new StubPort(DeviceKind.CardReader, DeviceStatus.Ok, honourCancellation: true)]);

    await Expect.ThrowsAsync<OperationCanceledException>(() => service.RunAsync(cts.Token));
});

await runner.CheckAsync("No registered devices means Offline", async () =>
{
    var report = await new DeviceSelfTestService([]).RunAsync();

    Expect.Equal(DeviceStatus.Offline, report.Overall, "Overall status");
});

await runner.CheckAsync("Devices are reported in kind order", async () =>
{
    var service = new DeviceSelfTestService(
    [
        new StubPort(DeviceKind.ReceiptPrinter, DeviceStatus.Ok),
        new StubPort(DeviceKind.CardReader, DeviceStatus.Ok),
    ]);

    var report = await service.RunAsync();

    Expect.Equal(DeviceKind.CardReader, report.Devices[0].Kind, "First device");
});

runner.Check("PAN is masked to first six and last four", () =>
    Expect.Equal("Card 411111******1111 used", SensitiveDataMasker.Mask("Card 4111111111111111 used")));

runner.Check("PIN, CVV, passwords and track data are redacted", () =>
{
    var text = SensitiveDataMasker.Mask("pin=1234 cvv=123 password=hunter2 %B4111111111111111^DOE/J^2512101?");
    foreach (var secret in new[] { "1234", "123 ", "hunter2", "4111111111111111" })
    {
        Expect.True(!text.Contains(secret, StringComparison.Ordinal), $"'{secret}' leaked: {text}");
    }
});

await runner.CheckAsync("Correlation ID survives await and child tasks", async () =>
{
    using var scope = CorrelationContext.Begin();
    var id = CorrelationContext.Current;
    await Task.Delay(5);
    Expect.Equal(id, await Task.Run(() => CorrelationContext.Current), "Correlation ID in child task");
});

runner.Check("Secrets round-trip, rotate keys and detect tampering", () =>
{
    var keys = new TestKeys();
    var protector = new SecretProtector(keys);
    var cipher = protector.Protect("hunter2");
    Expect.True(cipher.StartsWith("enc:v1:", StringComparison.Ordinal) && !cipher.Contains("hunter2", StringComparison.Ordinal), "Ciphertext format or leak.");
    Expect.Equal("hunter2", protector.Unprotect(cipher), "Decrypted value");

    keys.Rotate();
    Expect.Equal("hunter2", protector.Unprotect(cipher), "Old value after rotation");
    Expect.True(protector.Protect("x").StartsWith("enc:v2:", StringComparison.Ordinal), "New values must use the new key version.");

    Expect.Throws<ATM.Core.Errors.AtmException>(() => protector.Unprotect(cipher[..^4] + "AAAA"));
});

runner.Check("Config differences are detected and secrets flagged", () =>
{
    var changes = ConfigDiff.Compare(
        new Dictionary<string, string?> { ["A:Level"] = "Info", ["A:Password"] = "enc:v1:a" },
        new Dictionary<string, string?> { ["A:Level"] = "Debug", ["A:Password"] = "enc:v1:b" });
    Expect.Equal(2, changes.Count, "Changes");
    Expect.True(changes.Single(c => c.Key == "A:Password").IsSecret, "Password change must be flagged secret.");
});

return runner.Complete(args);

internal sealed class TestKeys : IKeyProvider
{
    private readonly List<byte[]> keys = [RandomNumberGenerator.GetBytes(32)];

    public int CurrentVersion => keys.Count;

    public KeyMaterial GetKey(int version) => new(version, keys[version - 1]);

    public int Rotate()
    {
        keys.Add(RandomNumberGenerator.GetBytes(32));
        return keys.Count;
    }
}

internal sealed class StubPort(DeviceKind kind, DeviceStatus status, bool throws = false, bool honourCancellation = false) : IDevicePort
{
    public DeviceKind Kind => kind;

    public Task<DeviceHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        if (honourCancellation)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return throws
            ? throw new InvalidOperationException("bus error")
            : Task.FromResult(new DeviceHealth(kind, status, status.ToString()));
    }
}
