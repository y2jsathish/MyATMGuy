using System.Security.Cryptography;
using System.Text.Json;
using ATM.Application.Audit;
using ATM.Application.Errors;
using ATM.Application.Terminal;
using ATM.Application.Updates;
using ATM.Core.Errors;

namespace ATM.Application.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string dir = Path.Combine(Path.GetTempPath(), "atm-update-" + Guid.NewGuid().ToString("N"));
    private readonly MemoryAuditStore store = new();
    private readonly ErrorCounters counters = new();

    public UpdateTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        key.Dispose();
        Directory.Delete(dir, true);
    }

    private UpdatePackageVerifier Verifier() => new(
        new EcdsaUpdateSignatureVerifier(key),
        new AuditService(store, TimeProvider.System, new AuditSigningKey(new byte[32]), new TerminalInfo("ATM-0001", "1", "t")),
        counters,
        "ATM.Host");

    private (byte[] Manifest, byte[] Signature) Package(string version = "1.2.0", string? minimum = null, string product = "ATM.Host", Action<List<UpdateFile>>? tweak = null)
    {
        var content = "payload"u8.ToArray();
        File.WriteAllBytes(Path.Combine(dir, "app.dll"), content);
        var files = new List<UpdateFile> { new("app.dll", Convert.ToHexString(SHA256.HashData(content)), content.Length) };
        tweak?.Invoke(files);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new UpdateManifest(product, version, minimum, DateTimeOffset.UtcNow, files), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return (bytes, key.SignData(bytes, HashAlgorithmName.SHA256));
    }

    private async Task<UpdateVerification> RunAsync((byte[] Manifest, byte[] Signature) package, string current = "1.1.0") =>
        await Verifier().VerifyAsync(package.Manifest, package.Signature, dir, Version.Parse(current));

    [Fact]
    public async Task ValidPackage_IsAcceptedAndAudited()
    {
        var result = await RunAsync(Package());

        result.Accepted.Should().BeTrue();
        (await store.ReadAllAsync()).Should().Contain(e => e.Action == "UpdateVerified");
        counters.Total.Should().Be(0);
    }

    [Fact]
    public async Task TamperedManifest_IsRejected()
    {
        var (manifest, signature) = Package();
        manifest[5] ^= 0x01;

        var result = await RunAsync((manifest, signature));

        result.Accepted.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("signature");
        counters.Snapshot().Should().ContainKey(ErrorCatalogue.UpdateRejected.Code);
        (await store.ReadAllAsync()).Should().Contain(e => e.Action == "UpdateRejected");
    }

    [Fact]
    public async Task ManifestSignedByAnotherKey_IsRejected()
    {
        var (manifest, _) = Package();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var result = await RunAsync((manifest, other.SignData(manifest, HashAlgorithmName.SHA256)));

        result.Accepted.Should().BeFalse();
    }

    [Fact]
    public async Task GarbageSignature_IsRejectedWithoutThrowing()
    {
        var (manifest, _) = Package();

        var result = await RunAsync((manifest, [1, 2, 3]));

        result.Accepted.Should().BeFalse();
    }

    [Fact]
    public async Task ChangedFile_IsRejected()
    {
        var package = Package();
        File.WriteAllBytes(Path.Combine(dir, "app.dll"), "payloaX"u8.ToArray());

        var result = await RunAsync(package);

        result.Errors.Should().ContainSingle().Which.Should().Contain("hash");
    }

    [Fact]
    public async Task MissingFile_IsRejected()
    {
        var package = Package(tweak: files => files.Add(new UpdateFile("extra.dll", new string('0', 64), 1)));

        var result = await RunAsync(package);

        result.Errors.Should().Contain(e => e.Contains("extra.dll") && e.Contains("missing"));
    }

    [Fact]
    public async Task UnlistedFileInFolder_IsRejected()
    {
        var package = Package();
        File.WriteAllText(Path.Combine(dir, "sneaky.dll"), "x");

        var result = await RunAsync(package);

        result.Errors.Should().Contain(e => e.Contains("sneaky.dll"));
    }

    [Theory]
    [InlineData("1.1.0")]
    [InlineData("1.0.9")]
    public async Task SameOrOlderVersion_IsRejected(string version)
    {
        var result = await RunAsync(Package(version));

        result.Errors.Should().Contain(e => e.Contains("not newer"));
    }

    [Fact]
    public async Task UpdateNeedingANewerBase_IsRejected()
    {
        var result = await RunAsync(Package("2.0.0", minimum: "1.5.0"));

        result.Errors.Should().Contain(e => e.Contains("at least version 1.5.0"));
    }

    [Fact]
    public async Task OtherProduct_IsRejected()
    {
        var result = await RunAsync(Package(product: "Something.Else"));

        result.Errors.Should().Contain(e => e.Contains("Something.Else"));
    }

    [Fact]
    public async Task PathEscapingTheFolder_IsRejected()
    {
        var package = Package(tweak: files => files.Add(new UpdateFile("../outside.dll", new string('0', 64), 1)));

        var result = await RunAsync(package);

        result.Errors.Should().Contain(e => e.Contains("outside"));
    }
}
