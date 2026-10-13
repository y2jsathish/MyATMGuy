using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using ATM.Application.Errors;
using ATM.Application.Events;
using ATM.Application.Monitoring;
using ATM.Infrastructure.Monitoring;
using ATM.Infrastructure.Resilience;
using ATM.TestApp.Common;
using Microsoft.Extensions.Logging.Abstractions;

// Starts the real monitoring API on a loopback port inside this process and exercises it over HTTP.
var runner = new CheckRunner("ATM.Monitoring.Api");
const string Key = "testapp-key";

var port = FreePort();
var work = Path.Combine(Path.GetTempPath(), $"atm-mon-{Guid.NewGuid():N}");
Directory.CreateDirectory(work);
var baseUrl = $"http://127.0.0.1:{port}/";

var server = new Thread(() => ATM.Monitoring.Api.ProgramEntry.Run(
[
    "--urls", baseUrl.TrimEnd('/'),
    "--environment", "Development",
    $"--Monitoring:ApiKey={Key}",
    $"--Monitoring:ConnectionString=Data Source={Path.Combine(work, "monitoring.db")}",
])) { IsBackground = true };
server.Start();

using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };
for (var i = 0; i < 100; i++)
{
    try
    {
        if ((await http.GetAsync("health")).IsSuccessStatusCode)
        {
            break;
        }
    }
    catch (HttpRequestException)
    {
        await Task.Delay(100);
    }
}

await runner.CheckAsync("Health endpoint is open and the API refuses calls without a key", async () =>
{
    Expect.Equal(HttpStatusCode.OK, (await http.GetAsync("health")).StatusCode, "GET /health");
    Expect.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("api/terminals")).StatusCode, "GET /api/terminals without key");
});

using var authorised = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };
authorised.DefaultRequestHeaders.Add("X-Api-Key", Key);

await runner.CheckAsync("Heartbeat registers the terminal; invalid ids are rejected", async () =>
{
    var ok = await authorised.PostAsJsonAsync("api/heartbeat", new HeartbeatRequest("ATM-0042", "1.0", "pc", "Dev", DateTimeOffset.UtcNow, 10, 3));
    Expect.Equal(HttpStatusCode.Accepted, ok.StatusCode, "POST heartbeat");
    var list = (await authorised.GetFromJsonAsync<List<TerminalSummary>>("api/terminals"))!;
    Expect.Equal(3, list.Single(t => t.TerminalId == "ATM-0042").ErrorTotal, "Error total");

    var bad = await authorised.PostAsJsonAsync("api/heartbeat", new HeartbeatRequest("bad id", "1", "pc", "Dev", DateTimeOffset.UtcNow, 1, 0));
    Expect.Equal(HttpStatusCode.BadRequest, bad.StatusCode, "POST heartbeat with invalid id");
});

await runner.CheckAsync("Terminal client queues while offline and replays in order", async () =>
{
    await using var bus = new InProcessEventBus(new InMemoryDeadLetterStore(), new ErrorCounters(), TimeProvider.System);
    var pipelines = new ResiliencePipelines(bus, NullLogger<ResiliencePipelines>.Instance, new ResilienceOptions { RetryAttempts = 1, RetryBaseDelay = TimeSpan.FromMilliseconds(1), MinimumThroughput = 1000 });
    var outbox = new Outbox(Path.Combine(work, "outbox.jsonl"), 100);

    var offline = new MonitoringClient(new HttpClient(new OfflineHandler()) { BaseAddress = new Uri(baseUrl) }, pipelines, outbox);
    await offline.SendHeartbeatAsync(new HeartbeatRequest("ATM-0043", "1.0", "pc", "Dev", DateTimeOffset.UtcNow, 1, 1));
    await offline.SendHeartbeatAsync(new HeartbeatRequest("ATM-0043", "1.0", "pc", "Dev", DateTimeOffset.UtcNow, 2, 2));
    Expect.Equal(2, outbox.Count, "Queued while offline");

    var online = new MonitoringClient(authorised, pipelines, outbox);
    Expect.Equal(2, await online.ReplayQueuedAsync(), "Replayed");
    Expect.Equal(0, outbox.Count, "Queue after replay");
    var list = (await authorised.GetFromJsonAsync<List<TerminalSummary>>("api/terminals"))!;
    Expect.Equal(2, list.Single(t => t.TerminalId == "ATM-0043").ErrorTotal, "Newest heartbeat applied last");
});

await runner.CheckAsync("OpenAPI document describes the endpoints", async () =>
{
    var spec = await http.GetStringAsync("swagger/v1/swagger.json");
    foreach (var path in new[] { "/api/heartbeat", "/api/health", "/api/errors", "/api/terminals" })
    {
        Expect.True(spec.Contains(path, StringComparison.Ordinal), $"{path} missing from the OpenAPI document.");
    }
});

var exit = runner.Complete(args);
try
{
    Directory.Delete(work, recursive: true);
}
catch (IOException)
{
    // the API process still holds the database; the temp folder is cleaned by the OS
}

return exit;

static int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

internal sealed class OfflineHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new HttpRequestException("offline");
}
