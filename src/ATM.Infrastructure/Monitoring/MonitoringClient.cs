using System.ComponentModel.DataAnnotations;
using System.Net.Http.Json;
using System.Text.Json;
using ATM.Application.Monitoring;
using ATM.Infrastructure.Resilience;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace ATM.Infrastructure.Monitoring;

public sealed class MonitoringOptions
{
    public const string SectionName = "Monitoring";

    /// <summary>HTTPS address of the monitoring API. Empty disables central monitoring.</summary>
    [RegularExpression("^(https://.*)?$", ErrorMessage = "BaseUrl must be empty or an https:// address.")]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Store as enc:... via atmcfg; the key name makes Prod reject a plaintext value.</summary>
    public string ApiKey { get; set; } = string.Empty;

    [Range(5, 3600)]
    public int HeartbeatSeconds { get; set; } = 60;

    public string OutboxPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "data", "monitoring-outbox.jsonl");

    [Range(10, 1_000_000)]
    public int MaxQueued { get; set; } = 10_000;

    public bool Enabled => !string.IsNullOrWhiteSpace(BaseUrl);
}

public sealed record OutboxItem(string Kind, string Payload);

/// <summary>Durable first-in-first-out queue of unsent monitoring messages, one JSON object per line. Oldest are dropped past the cap.</summary>
public sealed class Outbox(string path, int maxItems)
{
    private readonly object sync = new();

    public int Count
    {
        get
        {
            lock (sync)
            {
                return ReadAll().Count;
            }
        }
    }

    public void Enqueue(OutboxItem item)
    {
        lock (sync)
        {
            var items = ReadAll();
            items.Add(item);
            if (items.Count > maxItems)
            {
                items.RemoveRange(0, items.Count - maxItems);
            }

            WriteAll(items);
        }
    }

    public OutboxItem? Peek()
    {
        lock (sync)
        {
            return ReadAll().FirstOrDefault();
        }
    }

    public void RemoveFirst()
    {
        lock (sync)
        {
            var items = ReadAll();
            if (items.Count > 0)
            {
                items.RemoveAt(0);
                WriteAll(items);
            }
        }
    }

    private List<OutboxItem> ReadAll() =>
        File.Exists(path)
            ? [.. File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<OutboxItem>(l)!)]
            : [];

    private void WriteAll(List<OutboxItem> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllLines(path, items.Select(i => JsonSerializer.Serialize(i)));
    }
}

/// <summary>Sends terminal data to the monitoring API through the resilience pipeline. Anything that cannot be sent is queued and replayed in order.</summary>
public sealed class MonitoringClient(HttpClient http, ResiliencePipelines pipelines, Outbox outbox) : IMonitoringClient
{
    private static readonly Dictionary<string, string> Routes = new(StringComparer.Ordinal)
    {
        ["heartbeat"] = "api/heartbeat",
        ["health"] = "api/health",
        ["error"] = "api/errors",
    };

    public int QueuedCount => outbox.Count;

    public Task<bool> SendHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) => SendOrQueue("heartbeat", request, cancellationToken);

    public Task<bool> SendHealthAsync(HealthRequest request, CancellationToken cancellationToken = default) => SendOrQueue("health", request, cancellationToken);

    public Task<bool> SendErrorAsync(ErrorRequest request, CancellationToken cancellationToken = default) => SendOrQueue("error", request, cancellationToken);

    public async Task<int> ReplayQueuedAsync(CancellationToken cancellationToken = default)
    {
        var sent = 0;
        while (outbox.Peek() is { } item)
        {
            var result = await TrySend(item.Kind, item.Payload, cancellationToken).ConfigureAwait(false);
            if (result == SendResult.Retry)
            {
                break;
            }

            outbox.RemoveFirst(); // sent, or permanently rejected by the server (it would never be accepted)
            if (result == SendResult.Sent)
            {
                sent++;
            }
        }

        return sent;
    }

    private enum SendResult
    {
        Sent,
        Retry,
        Rejected,
    }

    private async Task<bool> SendOrQueue<T>(string kind, T request, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(request);

        // Keep order: while older items wait, new ones queue behind them.
        var result = outbox.Count == 0 ? await TrySend(kind, payload, cancellationToken).ConfigureAwait(false) : SendResult.Retry;
        if (result == SendResult.Retry)
        {
            outbox.Enqueue(new OutboxItem(kind, payload));
        }

        return result == SendResult.Sent;
    }

    private async Task<SendResult> TrySend(string kind, string payload, CancellationToken cancellationToken)
    {
        try
        {
            var status = await pipelines.Get(ResiliencePipelines.Monitoring).ExecuteAsync(async ct =>
            {
                using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                using var response = await http.PostAsync(Routes[kind], content, ct).ConfigureAwait(false);
                var code = (int)response.StatusCode;
                if (code >= 500 || code == 408 || code == 429)
                {
                    throw new HttpRequestException($"Server returned {code}", null, response.StatusCode);
                }

                return response.IsSuccessStatusCode;
            }, cancellationToken).ConfigureAwait(false);
            return status ? SendResult.Sent : SendResult.Rejected;
        }
        catch (Exception ex) when (ex is HttpRequestException or BrokenCircuitException or TimeoutRejectedException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return SendResult.Retry;
        }
    }
}
