using ATM.Application.Audit;
using ATM.Infrastructure.Audit;

namespace ATM.Infrastructure.Tests;

public class AuditStoreTests
{
    [Fact]
    public async Task FileStore_PersistsAndVerifiesAcrossInstances()
    {
        var path = Path.Combine(Path.GetTempPath(), $"audit-{Guid.NewGuid():N}.jsonl");
        try
        {
            var options = new AuditFileOptions { Path = path };
            await new AuditService(new JsonLinesAuditStore(options), TimeProvider.System).RecordAsync("a", "x", "t", "ok");
            var second = new AuditService(new JsonLinesAuditStore(options), TimeProvider.System);
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
            var service = new AuditService(new JsonLinesAuditStore(new AuditFileOptions { Path = path }), TimeProvider.System);
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
