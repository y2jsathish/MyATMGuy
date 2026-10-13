using System.Reflection;
using System.Text.Json;
using ATM.Core.Devices;
using ATM.Core.Entities;

namespace ATM.Application.Events;

/// <summary>Names an event contract. The name, not the CLR type, is what is stored and replayed, so types can move without breaking old data.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class EventContractAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>
/// Base for all events. Events are immutable records. Never change the meaning or remove a property of a published version;
/// add a new event (or a new optional property and bump <see cref="Version"/>) instead.
/// </summary>
public abstract record AtmEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();

    public DateTimeOffset OccurredUtc { get; init; } = DateTimeOffset.UtcNow;

    public Guid? CorrelationId { get; init; }

    public abstract int Version { get; }
}

[EventContract("device.status-changed")]
public sealed record DeviceStatusChanged(DeviceKind Kind, DeviceStatus Previous, DeviceStatus Current) : AtmEvent
{
    public override int Version => 1;
}

[EventContract("config.changed")]
public sealed record ConfigurationChanged(IReadOnlyList<string> Keys, string Source) : AtmEvent
{
    public override int Version => 1;
}

[EventContract("resilience.circuit-changed")]
public sealed record CircuitBreakerStateChanged(string Pipeline, string State) : AtmEvent
{
    public override int Version => 1;
}

[EventContract("health.changed")]
public sealed record HealthStateChanged(string Check, string Previous, string Current, string Message) : AtmEvent
{
    public override int Version => 1;
}

[EventContract("transaction.completed")]
public sealed record TransactionFinished(Guid TransactionId, TransactionStatus Status) : AtmEvent
{
    public override int Version => 1;
}

public static class EventTypes
{
    private static readonly Dictionary<string, Type> ByName = typeof(AtmEvent).Assembly.GetTypes()
        .Where(t => !t.IsAbstract && t.IsAssignableTo(typeof(AtmEvent)) && t.GetCustomAttribute<EventContractAttribute>() is not null)
        .ToDictionary(t => t.GetCustomAttribute<EventContractAttribute>()!.Name, t => t, StringComparer.Ordinal);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public static IReadOnlyCollection<string> Names => ByName.Keys;

    public static string NameOf(Type type) =>
        type.GetCustomAttribute<EventContractAttribute>()?.Name ?? throw new InvalidOperationException($"{type.Name} has no [EventContract].");

    public static Type? Find(string name) => ByName.GetValueOrDefault(name);

    public static string Serialize(AtmEvent evt) => JsonSerializer.Serialize(evt, evt.GetType(), Json);

    public static AtmEvent? Deserialize(string name, string payload) =>
        Find(name) is { } type ? (AtmEvent?)JsonSerializer.Deserialize(payload, type, Json) : null;
}

public interface IDeadLetterStore
{
    Task AddAsync(DeadLetter letter, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeadLetter>> ListAsync(CancellationToken cancellationToken = default);

    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class InMemoryDeadLetterStore : IDeadLetterStore
{
    private readonly List<DeadLetter> items = [];

    public Task AddAsync(DeadLetter letter, CancellationToken cancellationToken = default)
    {
        lock (items)
        {
            items.Add(letter);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DeadLetter>> ListAsync(CancellationToken cancellationToken = default)
    {
        lock (items)
        {
            return Task.FromResult<IReadOnlyList<DeadLetter>>([.. items]);
        }
    }

    public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (items)
        {
            items.RemoveAll(i => i.Id == id);
        }

        return Task.CompletedTask;
    }
}

public interface IEventBus
{
    /// <summary>Queues the event. Returns as soon as it is accepted; handlers run asynchronously.</summary>
    ValueTask PublishAsync<T>(T evt, CancellationToken cancellationToken = default)
        where T : AtmEvent;

    IDisposable Subscribe<T>(Func<T, CancellationToken, Task> handler, string? name = null)
        where T : AtmEvent;

    /// <summary>Events accepted but not yet fully handled.</summary>
    int Backlog { get; }
}
