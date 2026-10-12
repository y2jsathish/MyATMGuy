using ATM.Application.Audit;
using ATM.Application.Configuration;
using ATM.Application.Errors;
using ATM.Application.Terminal;
using ATM.Infrastructure.Audit;
using ATM.Infrastructure.Configuration;
using ATM.Infrastructure.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ATM.Infrastructure.Tests;

public sealed class ConfigurationTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), $"cfg-{Guid.NewGuid():N}");

    public ConfigurationTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private void Write(string name, string json) => File.WriteAllText(Path.Combine(dir, name), json);

    private SecretProtector Protector() => new(new FileKeyProvider(Path.Combine(dir, "keys")));

    [Fact]
    public void Layers_ApplyInDocumentedOrder()
    {
        Write("appsettings.json", """{"Terminal":{"TerminalId":"BASE","IdleTimeoutSeconds":30}}""");
        Write("appsettings.Sit.json", """{"Terminal":{"TerminalId":"SIT","IdleTimeoutSeconds":40}}""");
        Write("terminal.json", """{"Terminal":{"TerminalId":"TERM"}}""");

        var raw = AtmConfiguration.BuildRaw(dir, AtmEnvironment.Sit, reloadOnChange: false);

        raw["Terminal:TerminalId"].Should().Be("TERM");
        raw["Terminal:IdleTimeoutSeconds"].Should().Be("40");

        Environment.SetEnvironmentVariable("ATM_Terminal__IdleTimeoutSeconds", "99");
        try
        {
            AtmConfiguration.BuildRaw(dir, AtmEnvironment.Sit, reloadOnChange: false)["Terminal:IdleTimeoutSeconds"].Should().Be("99");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ATM_Terminal__IdleTimeoutSeconds", null);
        }
    }

    [Fact]
    public void Validation_NamesEveryInvalidKey()
    {
        var flat = new Dictionary<string, string?>
        {
            ["Terminal:TerminalId"] = "bad id!",
            ["Terminal:IdleTimeoutSeconds"] = "5",
            ["Logging:MinimumLevel"] = "Loud",
        };

        var errors = AtmConfiguration.Validate(flat, AtmEnvironment.Dev);

        errors.Should().Contain(e => e.StartsWith("Terminal:TerminalId", StringComparison.Ordinal));
        errors.Should().Contain(e => e.StartsWith("Terminal:IdleTimeoutSeconds", StringComparison.Ordinal));
        errors.Should().Contain(e => e.StartsWith("Logging:MinimumLevel", StringComparison.Ordinal));
    }

    [Fact]
    public void Prod_RejectsPlaintextSecretsAndDebugFeatures_ButDevAllows()
    {
        var flat = new Dictionary<string, string?> { ["Db:Password"] = "hunter2", ["Terminal:DebugFeatures"] = "true" };

        AtmConfiguration.Validate(flat, AtmEnvironment.Prod).Should().HaveCount(2);
        AtmConfiguration.Validate(flat, AtmEnvironment.Dev).Should().BeEmpty();
    }

    [Fact]
    public void Prod_AcceptsEncryptedSecret()
    {
        var flat = new Dictionary<string, string?> { ["Db:Password"] = Protector().Protect("hunter2") };

        AtmConfiguration.Validate(flat, AtmEnvironment.Prod).Should().BeEmpty();
    }

    [Fact]
    public void Decryption_HappensInMemoryOnly()
    {
        var protector = Protector();
        var cipher = protector.Protect("hunter2");
        Write("appsettings.json", "{\"Db\":{\"Password\":\"" + cipher + "\"}}");

        var raw = AtmConfiguration.BuildRaw(dir, AtmEnvironment.Dev, reloadOnChange: false);
        var decrypted = raw.WithDecryption(protector);

        decrypted["Db:Password"].Should().Be("hunter2");
        File.ReadAllText(Path.Combine(dir, "appsettings.json")).Should().NotContain("hunter2");
        raw["Db:Password"].Should().StartWith("enc:");
    }

    [Fact]
    public void Bootstrap_FallsBackToLastKnownGood_WhenFilesAreInvalid()
    {
        Write("appsettings.json", """{"Terminal":{"TerminalId":"GOOD-1"}}""");
        var good = ConfigBootstrap.Load(dir, AtmEnvironment.Dev, null);
        good.IsValid.Should().BeTrue();
        ConfigBootstrap.SaveLastKnownGood(dir, good.Raw);

        Write("appsettings.json", """{"Terminal":{"TerminalId":"bad id"}}""");
        var broken = ConfigBootstrap.Load(dir, AtmEnvironment.Dev, null);

        broken.UsedLastKnownGood.Should().BeTrue();
        broken.Errors.Should().NotBeEmpty();
        broken.Configuration["Terminal:TerminalId"].Should().Be("GOOD-1");
    }

    [Fact]
    public void Bootstrap_ReportsTamperedFile()
    {
        Write("appsettings.json", """{"Terminal":{"TerminalId":"A"}}""");
        ConfigIntegrity.WriteManifest(dir, ["appsettings.json"]);
        Write("appsettings.json", """{"Terminal":{"TerminalId":"B"}}""");

        var result = ConfigBootstrap.Load(dir, AtmEnvironment.Dev, null);

        result.Errors.Should().Contain(e => e.StartsWith("ATM-1014", StringComparison.Ordinal));
    }

    [Fact]
    public void FileKeyProvider_PersistsKeys_AndRotates()
    {
        var keysDir = Path.Combine(dir, "k");
        var first = new FileKeyProvider(keysDir);
        var v1 = first.GetKey(1).Key;

        first.Rotate().Should().Be(2);
        var reopened = new FileKeyProvider(keysDir);

        reopened.CurrentVersion.Should().Be(2);
        reopened.GetKey(1).Key.Should().Equal(v1);
        reopened.GetKey(2).Key.Should().NotEqual(v1);
    }

    [Fact]
    public void AuditKey_IsStableAcrossRotation()
    {
        var keys = new FileKeyProvider(Path.Combine(dir, "k"));
        var before = AuditKeyDerivation.Derive(keys).Value.ToArray();

        keys.Rotate();

        AuditKeyDerivation.Derive(keys).Value.ToArray().Should().Equal(before);
    }

    [Fact]
    public async Task Reload_AppliesGoodChange_AndRejectsBadOne()
    {
        Write("appsettings.json", """{"Logging":{"MinimumLevel":"Warning"}}""");
        var raw = AtmConfiguration.BuildRaw(dir, AtmEnvironment.Dev, reloadOnChange: false);
        var counters = new ErrorCounters();
        var audit = new AuditService(new InMemoryAuditStore(), TimeProvider.System, new AuditSigningKey(new byte[32]), new TerminalInfo("ATM-0001", "1", "t"));
        var governor = new ConfigGovernor(flat => AtmConfiguration.Validate(flat, AtmEnvironment.Dev), audit, counters);
        governor.Initialize(AtmConfiguration.Flatten(raw));
        var levels = new LogLevelController();
        levels.Apply(new LoggingOptions { MinimumLevel = "Warning" });
        var service = new ConfigReloadService(raw, governor, new LiveSettings(governor), levels, NullLogger<ConfigReloadService>.Instance);

        Write("appsettings.json", """{"Logging":{"MinimumLevel":"Debug"}}""");
        raw.Reload();
        (await service.ReloadAsync()).Applied.Should().BeTrue();
        levels.Root.MinimumLevel.Should().Be(Serilog.Events.LogEventLevel.Debug);

        Write("appsettings.json", """{"Logging":{"MinimumLevel":"Loud"}}""");
        raw.Reload();
        (await service.ReloadAsync()).Applied.Should().BeFalse();
        levels.Root.MinimumLevel.Should().Be(Serilog.Events.LogEventLevel.Debug);
        new LiveSettings(governor).Get<LoggingOptions>("Logging").MinimumLevel.Should().Be("Debug");
        counters.Total.Should().Be(1);
        (await audit.VerifyAsync()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Schema_DescribesRequiredFieldsRangesAndPatterns()
    {
        var schema = OptionsSchema.GenerateDocument((TerminalOptions.SectionName, typeof(TerminalOptions)), (LoggingOptions.SectionName, typeof(LoggingOptions)));

        schema.Should().Contain("\"TerminalId\"").And.Contain("\"required\"").And.Contain("\"minimum\": 10").And.Contain("\"pattern\"");
    }

    [Fact]
    public void CommittedSchema_MatchesGeneratedSchema()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "ATM.sln")))
        {
            root = Path.GetDirectoryName(root);
        }

        var committed = Path.Combine(root!, "docs", "config", "atm.schema.json");
        var generated = OptionsSchema.GenerateDocument((TerminalOptions.SectionName, typeof(TerminalOptions)), (LoggingOptions.SectionName, typeof(LoggingOptions)));

        File.ReadAllText(committed).ReplaceLineEndings("\n").TrimEnd().Should().Be(generated.ReplaceLineEndings("\n").TrimEnd(),
            "run 'atmcfg schema docs/config/atm.schema.json' after changing options classes");
    }

    [Fact]
    public void SampleConfigurationFiles_PassValidation()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "ATM.sln")))
        {
            root = Path.GetDirectoryName(root);
        }

        foreach (var file in Directory.GetFiles(Path.Combine(root!, "docs", "config", "samples"), "*.json"))
        {
            var raw = new ConfigurationBuilder().AddJsonFile(file).Build();
            AtmConfiguration.Validate(AtmConfiguration.Flatten(raw), AtmEnvironment.Dev).Should().BeEmpty(file);
        }
    }
}
