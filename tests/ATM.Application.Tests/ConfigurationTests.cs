using System.Security.Cryptography;
using ATM.Application.Audit;
using ATM.Application.Configuration;
using ATM.Application.Errors;
using ATM.Application.Terminal;
using ATM.Core.Entities;
using ATM.Core.Errors;

namespace ATM.Application.Tests;

internal sealed class InMemoryKeys : IKeyProvider
{
    private readonly List<byte[]> keys = [RandomNumberGenerator.GetBytes(32)];

    public int CurrentVersion => keys.Count;

    public KeyMaterial GetKey(int version) =>
        version >= 1 && version <= keys.Count ? new KeyMaterial(version, keys[version - 1]) : throw new FileNotFoundException("no such key");

    public int Rotate()
    {
        keys.Add(RandomNumberGenerator.GetBytes(32));
        return keys.Count;
    }
}

internal sealed class MemoryAuditStore : IAuditStore
{
    public List<AuditEntry> Items { get; } = [];

    public Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        Items.Add(entry);
        return Task.CompletedTask;
    }

    public Task<AuditEntry?> GetLastAsync(CancellationToken cancellationToken = default) => Task.FromResult(Items.LastOrDefault());

    public Task<IReadOnlyList<AuditEntry>> ReadAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AuditEntry>>([.. Items]);
}

public class SecretProtectorTests
{
    [Fact]
    public void RoundTrip_ReturnsOriginal_AndCiphertextHidesIt()
    {
        var protector = new SecretProtector(new InMemoryKeys());

        var cipher = protector.Protect("s3cr3t-Value");

        cipher.Should().StartWith("enc:v1:").And.NotContain("s3cr3t");
        protector.Unprotect(cipher).Should().Be("s3cr3t-Value");
    }

    [Fact]
    public void SameValue_GivesDifferentCiphertextEachTime()
    {
        var protector = new SecretProtector(new InMemoryKeys());

        protector.Protect("x").Should().NotBe(protector.Protect("x"));
    }

    [Fact]
    public void Rotation_KeepsOldValuesReadable_AndNewValuesUseNewVersion()
    {
        var keys = new InMemoryKeys();
        var protector = new SecretProtector(keys);
        var old = protector.Protect("one");

        keys.Rotate();
        var fresh = protector.Protect("two");

        fresh.Should().StartWith("enc:v2:");
        protector.Unprotect(old).Should().Be("one");
        protector.Unprotect(fresh).Should().Be("two");
    }

    [Fact]
    public void TamperedOrForeignCiphertext_IsRejectedWithCatalogueCode()
    {
        var protector = new SecretProtector(new InMemoryKeys());
        var cipher = protector.Protect("value");
        var tampered = cipher[..^3] + (cipher.EndsWith("AAA", StringComparison.Ordinal) ? "BBB" : "AAA");

        protector.Invoking(p => p.Unprotect(tampered)).Should().Throw<AtmException>().Which.Code.Should().Be(ErrorCatalogue.SecretUnavailable);
        new SecretProtector(new InMemoryKeys()).Invoking(p => p.Unprotect(cipher)).Should().Throw<AtmException>();
        protector.Invoking(p => p.Unprotect("plain")).Should().Throw<AtmException>();
    }

    [Theory]
    [InlineData("enc:v1:abc", true)]
    [InlineData("plain", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsProtected_RecognisesPrefix(string? value, bool expected) =>
        SecretProtector.IsProtected(value).Should().Be(expected);
}

public class SecretKeyTests
{
    [Theory]
    [InlineData("Db:Password", true)]
    [InlineData("Api:ApiKey", true)]
    [InlineData("Api:api_key", true)]
    [InlineData("Monitoring:Token", true)]
    [InlineData("ConnectionStrings:ConnectionString", true)]
    [InlineData("Terminal:TerminalId", false)]
    [InlineData("Logging:MinimumLevel", false)]
    public void IsSecretKey_MatchesByName(string key, bool expected) =>
        SecretKeys.IsSecretKey(key).Should().Be(expected);

    [Fact]
    public void FindPlaintext_IgnoresEncryptedAndEmptyValues()
    {
        var flat = new Dictionary<string, string?>
        {
            ["A:Password"] = "plain",
            ["B:Password"] = "enc:v1:xyz",
            ["C:Token"] = "",
            ["D:Name"] = "visible",
        };

        SecretKeys.FindPlaintext(flat).Should().BeEquivalentTo("A:Password");
    }
}

public class ConfigGovernorTests
{
    private static (ConfigGovernor Governor, MemoryAuditStore Store, ErrorCounters Counters) Create(Func<IReadOnlyDictionary<string, string?>, IReadOnlyList<string>>? validate = null)
    {
        var store = new MemoryAuditStore();
        var counters = new ErrorCounters();
        var audit = new AuditService(store, TimeProvider.System, new AuditSigningKey(new byte[32]), new TerminalInfo("ATM-0001", "1", "t"));
        var governor = new ConfigGovernor(validate ?? (_ => []), audit, counters);
        governor.Initialize(new Dictionary<string, string?> { ["Terminal:IdleTimeoutSeconds"] = "60", ["Db:Password"] = "enc:v1:old" });
        return (governor, store, counters);
    }

    [Fact]
    public async Task ValidChange_IsApplied_AndAuditedWithOldAndNewValues()
    {
        var (governor, store, _) = Create();

        var result = await governor.ApplyAsync(new Dictionary<string, string?> { ["Terminal:IdleTimeoutSeconds"] = "90", ["Db:Password"] = "enc:v1:old" }, "test");

        result.Applied.Should().BeTrue();
        governor.Current["Terminal:IdleTimeoutSeconds"].Should().Be("90");
        store.Items.Should().ContainSingle().Which.Outcome.Should().Contain("'60' -> '90'");
        store.Items[0].Category.Should().Be(AuditCategory.Config);
    }

    [Fact]
    public async Task SecretChange_IsAuditedAsHashesOnly()
    {
        var (governor, store, _) = Create();

        await governor.ApplyAsync(new Dictionary<string, string?> { ["Terminal:IdleTimeoutSeconds"] = "60", ["Db:Password"] = "enc:v1:newcipher" }, "test");

        var outcome = store.Items.Single().Outcome;
        outcome.Should().NotContain("newcipher").And.NotContain("old");
        outcome.Should().MatchRegex("changed [0-9A-F]{16} -> [0-9A-F]{16}");
    }

    [Fact]
    public async Task InvalidChange_IsRejected_KeepsLastKnownGood_RaisesHighError()
    {
        var (governor, store, counters) = Create(flat => flat["Terminal:IdleTimeoutSeconds"] == "1" ? ["Terminal:IdleTimeoutSeconds: out of range"] : []);

        var result = await governor.ApplyAsync(new Dictionary<string, string?> { ["Terminal:IdleTimeoutSeconds"] = "1" }, "test");

        result.Applied.Should().BeFalse();
        result.Errors.Should().ContainSingle();
        governor.Current["Terminal:IdleTimeoutSeconds"].Should().Be("60");
        counters.Snapshot().Should().ContainKey(ErrorCatalogue.ConfigurationRejected.Code);
        ErrorCatalogue.ConfigurationRejected.Severity.Should().Be(ErrorSeverity.High);
        store.Items.Should().ContainSingle().Which.Action.Should().Be("ConfigRejected");
    }

    [Fact]
    public async Task NoChange_WritesNoAuditEntries()
    {
        var (governor, store, _) = Create();

        await governor.ApplyAsync(new Dictionary<string, string?>(governor.Current), "test");

        store.Items.Should().BeEmpty();
    }
}

public class ConfigIntegrityTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), $"cfg-{Guid.NewGuid():N}");

    public ConfigIntegrityTests()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "appsettings.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "terminal.json"), "{\"a\":1}");
    }

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Fact]
    public void Untouched_Verifies()
    {
        ConfigIntegrity.WriteManifest(dir, ["appsettings.json", "terminal.json"]);

        ConfigIntegrity.Verify(dir).IsValid.Should().BeTrue();
    }

    [Fact]
    public void EditedFile_IsDetected()
    {
        ConfigIntegrity.WriteManifest(dir, ["appsettings.json", "terminal.json"]);
        File.WriteAllText(Path.Combine(dir, "terminal.json"), "{\"a\":2}");

        ConfigIntegrity.Verify(dir).Problems.Should().ContainSingle().Which.Should().Contain("terminal.json");
    }

    [Fact]
    public void DeletedFile_AndMissingManifest_AreDetected()
    {
        ConfigIntegrity.Verify(dir).IsValid.Should().BeFalse();
        ConfigIntegrity.WriteManifest(dir, ["appsettings.json", "terminal.json"]);
        File.Delete(Path.Combine(dir, "terminal.json"));

        ConfigIntegrity.Verify(dir).IsValid.Should().BeFalse();
    }
}

public class EnvironmentTests
{
    [Fact]
    public void Prod_ForbidsDebugFeatures() => AtmEnvironment.Prod.AllowsDebugFeatures().Should().BeFalse();

    [Theory]
    [InlineData(AtmEnvironment.Dev)]
    [InlineData(AtmEnvironment.Sit)]
    [InlineData(AtmEnvironment.Uat)]
    [InlineData(AtmEnvironment.Pilot)]
    public void NonProd_AllowsDebugFeatures(AtmEnvironment env) => env.AllowsDebugFeatures().Should().BeTrue();
}
