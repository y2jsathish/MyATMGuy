using ATM.Application.Audit;
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
        var service = new AuditService(store, TimeProvider.System);
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
    public async Task BlankActor_IsRejected()
    {
        var (service, _) = await Seeded();

        await service.Invoking(s => s.RecordAsync(" ", "a", "t", "o")).Should().ThrowAsync<ArgumentException>();
    }
}
