using ATM.Application.Audit;
using ATM.Application.Terminal;
using ATM.Core.Entities;

namespace ATM.Application.Tests;

public class AuditTests
{
    private sealed class ListStore : IAuditStore
    {
        public List<AuditEntry> Items { get; } = [];

        public Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            Items.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEntry>> ReadAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AuditEntry>>([.. Items]);
    }

    private static async Task<(AuditService Service, ListStore Store)> Seeded()
    {
        var store = new ListStore();
        var service = new AuditService(store, TimeProvider.System, new AuditSigningKey(new byte[32]), new TerminalInfo("ATM-0001", "1.0", "test"));
        await service.RecordAsync("system", "Start", "terminal", "ok");
        await service.RecordAsync("tech", "SelfTest", "devices", "ok");
        await service.RecordAsync("tech", "Dispense", "cash", "fail");
        return (service, store);
    }

    [Fact]
    public async Task Entries_AreChained()
    {
        var (_, store) = await Seeded();

        store.Items[0].PreviousHash.Should().BeNull();
        store.Items[1].PreviousHash.Should().Be(store.Items[0].Hash);
        store.Items[2].PreviousHash.Should().Be(store.Items[1].Hash);
    }

    [Fact]
    public async Task Untouched_ChainVerifies()
    {
        var (service, _) = await Seeded();

        (await service.VerifyAsync()).Should().Be(new AuditVerification(true, 3, null));
    }

    [Fact]
    public async Task EditedEntry_IsDetected()
    {
        var (service, store) = await Seeded();
        store.Items[1] = store.Items[1] with { Outcome = "changed" };

        var result = await service.VerifyAsync();

        result.IsValid.Should().BeFalse();
        result.FirstBrokenIndex.Should().Be(1);
    }

    [Fact]
    public async Task RemovedEntry_IsDetected()
    {
        var (service, store) = await Seeded();
        store.Items.RemoveAt(1);

        (await service.VerifyAsync()).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task WrongKey_FailsVerification()
    {
        var (_, store) = await Seeded();
        var other = new AuditService(store, TimeProvider.System, new AuditSigningKey(Enumerable.Repeat((byte)7, 32).ToArray()), new TerminalInfo("ATM-0001", "1.0", "test"));

        (await other.VerifyAsync()).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task ReorderedEntries_AreDetected()
    {
        var (service, store) = await Seeded();
        (store.Items[1], store.Items[2]) = (store.Items[2], store.Items[1]);

        (await service.VerifyAsync()).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Entry_CarriesTerminalCategoryAndAmbientCorrelation()
    {
        var (service, _) = await Seeded();
        var id = ATM.Core.ValueObjects.CorrelationId.New();
        ATM.Core.Entities.AuditEntry entry;
        using (ATM.Application.Correlation.CorrelationContext.Begin(id))
        {
            entry = await service.RecordAsync("op", "Login", "ui", "ok", ATM.Core.Entities.AuditCategory.Security);
        }

        entry.Terminal.Should().Be("ATM-0001");
        entry.Category.Should().Be(ATM.Core.Entities.AuditCategory.Security);
        entry.CorrelationId.Should().Be(id);
    }

    [Fact]
    public async Task BlankActor_IsRejected()
    {
        var (service, _) = await Seeded();

        await service.Invoking(s => s.RecordAsync(" ", "a", "t", "o")).Should().ThrowAsync<ArgumentException>();
    }
}
