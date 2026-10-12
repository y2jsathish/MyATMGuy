using System.Net;
using System.Net.Http.Json;
using ATM.Application.Monitoring;
using ATM.Infrastructure.Monitoring;
using ATM.Infrastructure.Resilience;
using ATM.Application.Errors;
using ATM.Application.Events;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ATM.Monitoring.Api.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string Key = "test-key-123";

    private readonly string db = Path.Combine(Path.GetTempPath(), $"monapi-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Monitoring:ConnectionString", $"Data Source={db}");
        builder.UseSetting("Monitoring:ApiKey", Key);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(db))
        {
            File.Delete(db);
        }
    }

    public HttpClient Authorized()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", Key);
        return client;
    }
}

public class MonitoringApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public MonitoringApiTests(ApiFactory factory) => this.factory = factory;

    private static HeartbeatRequest Heartbeat(string id = "ATM-0001", int errors = 0) =>
        new(id, "1.0.0", "pc-1", "Dev", DateTimeOffset.UtcNow, 120, errors);

    [Fact]
    public async Task MissingOrWrongKey_IsUnauthorized()
    {
        using var anonymous = factory.CreateClient();
        (await anonymous.PostAsJsonAsync("/api/heartbeat", Heartbeat())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var wrong = factory.CreateClient();
        wrong.DefaultRequestHeaders.Add("X-Api-Key", "nope");
        (await wrong.GetAsync("/api/terminals")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NoConfiguredKey_RefusesEverything_FailClosed()
    {
        using var closed = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Testing").UseSetting("Monitoring:ConnectionString", "Data Source=:memory:"));
        using var client = closed.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "");

        (await client.GetAsync("/api/terminals")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task HealthEndpoint_IsOpen() =>
        (await factory.CreateClient().GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);

    [Fact]
    public async Task Heartbeat_RegistersTerminal_AndUpdatesIt()
    {
        using var client = factory.Authorized();

        (await client.PostAsJsonAsync("/api/heartbeat", Heartbeat("ATM-0100", 1))).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await client.PostAsJsonAsync("/api/heartbeat", Heartbeat("ATM-0100", 5))).StatusCode.Should().Be(HttpStatusCode.Accepted);

        var terminals = (await client.GetFromJsonAsync<List<TerminalSummary>>("/api/terminals"))!;
        terminals.Where(t => t.TerminalId == "ATM-0100").Should().ContainSingle().Which.ErrorTotal.Should().Be(5);
    }

    [Fact]
    public async Task Health_SetsOverallState()
    {
        using var client = factory.Authorized();
        await client.PostAsJsonAsync("/api/heartbeat", Heartbeat("ATM-0101"));

        var response = await client.PostAsJsonAsync("/api/health", new HealthRequest("ATM-0101", "Degraded", [new HealthEntryDto("Disk", "Degraded", "low", DateTimeOffset.UtcNow)], DateTimeOffset.UtcNow));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await client.GetFromJsonAsync<List<TerminalSummary>>("/api/terminals"))!.Single(t => t.TerminalId == "ATM-0101").Overall.Should().Be("Degraded");
    }

    [Fact]
    public async Task Errors_AreAccepted_AndMasked()
    {
        using var client = factory.Authorized();

        var response = await client.PostAsJsonAsync("/api/errors", new ErrorRequest("ATM-0102", "ATM-4001", "High", "card 4111111111111111 failed", Guid.NewGuid(), DateTimeOffset.UtcNow));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var scope = factory.Services.CreateScope();
        var stored = scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Errors.Single(e => e.TerminalId == "ATM-0102");
        stored.Message.Should().NotContain("4111111111111111").And.Contain("411111******1111");
    }

    [Theory]
    [InlineData("bad id!")]
    [InlineData("")]
    public async Task InvalidTerminalId_IsRejected(string id)
    {
        using var client = factory.Authorized();

        (await client.PostAsJsonAsync("/api/heartbeat", Heartbeat(id))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task OpenApiDocument_ListsEveryEndpoint_AndMatchesCommittedSpec()
    {
        using var client = factory.CreateClient();
        var spec = await client.GetStringAsync("/swagger/v1/swagger.json");

        spec.Should().Contain("/api/heartbeat").And.Contain("/api/health").And.Contain("/api/errors").And.Contain("/api/terminals").And.Contain("X-Api-Key");

        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "ATM.sln")))
        {
            root = Path.GetDirectoryName(root);
        }

        var committed = Path.Combine(root!, "docs", "api", "monitoring-openapi.json");
        if (Environment.GetEnvironmentVariable("ATM_UPDATE_SPEC") == "1")
        {
            File.WriteAllText(committed, spec);
        }

        File.ReadAllText(committed).ReplaceLineEndings("\n").TrimEnd().Should().Be(spec.ReplaceLineEndings("\n").TrimEnd(),
            "regenerate with ATM_UPDATE_SPEC=1 dotnet test after changing the API");
    }

    [Fact]
    public async Task TerminalClient_EndToEnd_QueuesWhileOffline_AndDeliversInOrder()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"e2e-{Guid.NewGuid():N}");
        try
        {
            await using var bus = new InProcessEventBus(new InMemoryDeadLetterStore(), new ErrorCounters(), TimeProvider.System);
            var pipelines = new ResiliencePipelines(bus, NullLogger<ResiliencePipelines>.Instance, new ResilienceOptions { RetryAttempts = 1, RetryBaseDelay = TimeSpan.FromMilliseconds(1), MinimumThroughput = 1000 });
            var outbox = new Outbox(Path.Combine(dir, "outbox.jsonl"), 100);

            var offline = new HttpClient(new FailingHandler()) { BaseAddress = new Uri("https://monitor.test/") };
            var offlineClient = new MonitoringClient(offline, pipelines, outbox);
            await offlineClient.SendHeartbeatAsync(Heartbeat("ATM-0200", 1));
            await offlineClient.SendHeartbeatAsync(Heartbeat("ATM-0200", 2));
            outbox.Count.Should().Be(2);

            var onlineHttp = factory.Authorized();
            var onlineClient = new MonitoringClient(onlineHttp, pipelines, outbox);
            (await onlineClient.ReplayQueuedAsync()).Should().Be(2);

            outbox.Count.Should().Be(0);
            (await onlineHttp.GetFromJsonAsync<List<TerminalSummary>>("/api/terminals"))!.Single(t => t.TerminalId == "ATM-0200").ErrorTotal.Should().Be(2, "the newest heartbeat is applied last");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }
}
