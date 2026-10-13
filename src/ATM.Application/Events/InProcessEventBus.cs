using System.Collections.Concurrent;
using System.Threading.Channels;
using ATM.Application.Correlation;
using ATM.Application.Errors;
using ATM.Core.Entities;
using ATM.Core.Errors;

namespace ATM.Application.Events;

public sealed class EventBusOptions
{
    public int MaxAttempts { get; set; } = 3;

    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);
}

/// <summary>
/// In-process publish/subscribe. Each event type has its own queue and a single consumer, so order is preserved per type.
/// A failing handler is retried, then its event goes to the dead-letter store; other handlers and later events are unaffected.
/// </summary>
public sealed class InProcessEventBus(IDeadLetterStore deadLetters, IErrorCounters counters, TimeProvider clock, EventBusOptions? options = null) : IEventBus, IAsyncDisposable
{
    private readonly EventBusOptions settings = options ?? new EventBusOptions();
    private readonly ConcurrentDictionary<Type, Lane> lanes = new();
    private readonly CancellationTokenSource stopping = new();
    private int backlog;
    private int disposed;

    public int Backlog => Volatile.Read(ref backlog);

    public ValueTask PublishAsync<T>(T evt, CancellationToken cancellationToken = default)
        where T : AtmEvent
    {
        ArgumentNullException.ThrowIfNull(evt);
        var tagged = evt.CorrelationId is null && CorrelationContext.Current is { } id ? evt with { CorrelationId = id.Value } : evt;
        var lane = LaneFor(typeof(T));
        Interlocked.Increment(ref backlog);
        if (!lane.Channel.Writer.TryWrite(tagged))
        {
            Interlocked.Decrement(ref backlog);
            throw new InvalidOperationException("The event bus has been stopped.");
        }

        return ValueTask.CompletedTask;
    }

    public IDisposable Subscribe<T>(Func<T, CancellationToken, Task> handler, string? name = null)
        where T : AtmEvent
    {
        var entry = new Handler(name ?? handler.Method.Name, (e, ct) => handler((T)e, ct));
        LaneFor(typeof(T)).Add(entry);
        return new Subscription(() => LaneFor(typeof(T)).Remove(entry));
    }

    /// <summary>Waits until every accepted event has been handled (or dead-lettered).</summary>
    public async Task DrainAsync(TimeSpan? timeout = null)
    {
        var deadline = clock.GetUtcNow() + (timeout ?? TimeSpan.FromSeconds(10));
        while (Backlog > 0 && clock.GetUtcNow() < deadline)
        {
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        foreach (var lane in lanes.Values)
        {
            lane.Channel.Writer.TryComplete();
        }

        await DrainAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await stopping.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(lanes.Values.Select(l => l.Consumer)).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        stopping.Dispose();
    }

    private Lane LaneFor(Type type) => lanes.GetOrAdd(type, t => new Lane(Run));

    private async Task Run(Lane lane)
    {
        await foreach (var evt in lane.Channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            foreach (var handler in lane.Handlers)
            {
                await Deliver(handler, evt).ConfigureAwait(false);
            }

            Interlocked.Decrement(ref backlog);
        }
    }

    private async Task Deliver(Handler handler, AtmEvent evt)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= Math.Max(1, settings.MaxAttempts); attempt++)
        {
            try
            {
                using (evt.CorrelationId is { } id ? CorrelationContext.Begin(new(id)) : null)
                {
                    await handler.Invoke(evt, stopping.Token).ConfigureAwait(false);
                }

                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                if (attempt < settings.MaxAttempts && settings.RetryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(settings.RetryDelay).ConfigureAwait(false);
                }
            }
        }

        counters.Record(ErrorCatalogue.Unknown);
        await deadLetters.AddAsync(
            new DeadLetter(Guid.NewGuid(), EventTypes.NameOf(evt.GetType()), EventTypes.Serialize(evt), handler.Name, last?.Message ?? "unknown", settings.MaxAttempts, clock.GetUtcNow())).ConfigureAwait(false);
    }

    private sealed record Handler(string Name, Func<AtmEvent, CancellationToken, Task> Invoke);

    private sealed class Lane
    {
        private Handler[] handlers = [];

        public Lane(Func<Lane, Task> run)
        {
            Channel = System.Threading.Channels.Channel.CreateUnbounded<AtmEvent>(new UnboundedChannelOptions { SingleReader = true });
            Consumer = Task.Run(() => run(this));
        }

        public Channel<AtmEvent> Channel { get; }

        public Task Consumer { get; }

        public Handler[] Handlers => Volatile.Read(ref handlers);

        public void Add(Handler h)
        {
            lock (this)
            {
                handlers = [.. handlers, h];
            }
        }

        public void Remove(Handler h)
        {
            lock (this)
            {
                handlers = [.. handlers.Where(x => x != h)];
            }
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

/// <summary>Replays dead-lettered events through the bus and removes each one once it has been accepted again.</summary>
public sealed class DeadLetterReplayer(IDeadLetterStore store, IEventBus bus)
{
    public async Task<int> ReplayAllAsync(CancellationToken cancellationToken = default)
    {
        var replayed = 0;
        foreach (var letter in await store.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            var evt = EventTypes.Deserialize(letter.EventType, letter.Payload);
            if (evt is null)
            {
                continue;
            }

            await ((dynamic)bus).PublishAsync((dynamic)evt, cancellationToken).ConfigureAwait(false);
            await store.RemoveAsync(letter.Id, cancellationToken).ConfigureAwait(false);
            replayed++;
        }

        return replayed;
    }
}
