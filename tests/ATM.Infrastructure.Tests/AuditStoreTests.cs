using ATM.Application.Audit;
using ATM.Application.Terminal;
using ATM.Infrastructure.Audit;

namespace ATM.Infrastructure.Tests;

public class AuditStoreTests
{
    private static readonly AuditSigningKey Key = new(new byte[32]);
    private static readonly TerminalInfo Terminal = new("ATM-0001", "1.0", "test");

    [Fact]
    public async Task FileStore_PersistsAndVerifiesAcrossInstances()
    {
        var path = Path.Combine(Path.GetTempPath(), $"audit-{Guid.NewGuid():N}.jsonl");
        try
        {
            var options = new AuditFileOptions { Path = path };
            await new AuditService(new JsonLinesAuditStore(options), TimeProvider.System, Key, Terminal).RecordAsync("a", "x", "t", "ok");
            var second = new AuditService(new JsonLinesAuditStore(options), TimeProvider.System, Key, Terminal);
            await second.RecordAsync("a", "y", "t", "ok");

            (await second.VerifyAsync()).Should().Be(new AuditVerification(true, 2, null));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FileStore_DetectsEditedLine()
    {
        var path = Path.Combine(Path.GetTempPath(), $"audit-{Guid.NewGuid():N}.jsonl");
        try
        {
            var service = new AuditService(new JsonLinesAuditStore(new AuditFileOptions { Path = path }), TimeProvider.System, Key, Terminal);
            await service.RecordAsync("a", "x", "t", "ok");
            await service.RecordAsync("a", "y", "t", "ok");
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"Action\":\"x\"", "\"Action\":\"z\"", StringComparison.Ordinal));

            (await service.VerifyAsync()).IsValid.Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
