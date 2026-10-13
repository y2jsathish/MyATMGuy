using System.Security.Cryptography;
using System.Text;
using ATM.Application.Masking;
using ATM.Application.Monitoring;
using ATM.Core.ValueObjects;
using ATM.Monitoring.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

var startupOptions = builder.Configuration.GetSection(MonitoringApiOptions.SectionName).Get<MonitoringApiOptions>() ?? new MonitoringApiOptions();
if (string.IsNullOrEmpty(startupOptions.ApiKey) && !builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
{
    throw new InvalidOperationException("Monitoring:ApiKey must be configured outside Development.");
}

builder.Services.Configure<MonitoringApiOptions>(builder.Configuration.GetSection(MonitoringApiOptions.SectionName));
builder.Services.AddDbContext<MonitoringDbContext>((sp, o) =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MonitoringApiOptions>>().Value;
    if (string.Equals(options.Provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
    {
        o.UseSqlServer(options.ConnectionString);
    }
    else
    {
        o.UseSqlite(options.ConnectionString);
    }
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "ATM Platform Monitoring API", Version = "v1", Description = "Receives heartbeats, health and errors from ATM terminals." });
    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = "X-Api-Key" });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" } }] = [],
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<MonitoringDbContext>().Database.EnsureCreated();
}

app.UseSwagger();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription();

var api = app.MapGroup("/api").AddEndpointFilter(async (context, next) =>
{
    // Fail closed: with no configured key every call is refused.
    var configured = context.HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<MonitoringApiOptions>>().Value.ApiKey;
    var supplied = context.HttpContext.Request.Headers["X-Api-Key"].ToString();
    if (string.IsNullOrEmpty(configured) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(configured)))
    {
        return Results.Unauthorized();
    }

    return await next(context);
});

static bool ValidTerminal(string id) => TerminalId.TryCreate(id, out _);

api.MapPost("/heartbeat", async (HeartbeatRequest request, MonitoringDbContext db, CancellationToken ct) =>
{
    if (!ValidTerminal(request.TerminalId))
    {
        return Results.BadRequest(new { error = "Invalid terminal id." });
    }

    var terminal = await db.Terminals.FindAsync([request.TerminalId], ct);
    if (terminal is null)
    {
        db.Terminals.Add(terminal = new TerminalRow { TerminalId = request.TerminalId });
    }

    terminal.Version = request.Version;
    terminal.Machine = request.Machine;
    terminal.Environment = request.Environment;
    terminal.LastHeartbeatUtc = request.SentUtc;
    terminal.ErrorTotal = request.ErrorTotal;
    await db.SaveChangesAsync(ct);
    return Results.Accepted();
}).WithName("PostHeartbeat").WithSummary("Record a terminal heartbeat.");

api.MapPost("/health", async (HealthRequest request, MonitoringDbContext db, CancellationToken ct) =>
{
    if (!ValidTerminal(request.TerminalId))
    {
        return Results.BadRequest(new { error = "Invalid terminal id." });
    }

    var terminal = await db.Terminals.FindAsync([request.TerminalId], ct);
    if (terminal is null)
    {
        db.Terminals.Add(terminal = new TerminalRow { TerminalId = request.TerminalId, LastHeartbeatUtc = request.SentUtc });
    }

    terminal.Overall = request.Overall;
    terminal.HealthJson = SensitiveDataMasker.Mask(System.Text.Json.JsonSerializer.Serialize(request.Entries));
    await db.SaveChangesAsync(ct);
    return Results.Accepted();
}).WithName("PostHealth").WithSummary("Record the latest health report of a terminal.");

api.MapPost("/errors", async (ErrorRequest request, MonitoringDbContext db, CancellationToken ct) =>
{
    if (!ValidTerminal(request.TerminalId))
    {
        return Results.BadRequest(new { error = "Invalid terminal id." });
    }

    db.Errors.Add(new ErrorRow
    {
        TerminalId = request.TerminalId,
        Code = request.Code,
        Severity = request.Severity,
        Message = SensitiveDataMasker.Mask(request.Message),
        CorrelationId = request.CorrelationId,
        OccurredUtc = request.OccurredUtc,
    });
    await db.SaveChangesAsync(ct);
    return Results.Accepted();
}).WithName("PostError").WithSummary("Record an error raised on a terminal.");

api.MapGet("/terminals", async (MonitoringDbContext db, CancellationToken ct) =>
{
    var rows = await db.Terminals.AsNoTracking().OrderBy(t => t.TerminalId).ToListAsync(ct);
    return Results.Ok(rows.Select(t => new TerminalSummary(t.TerminalId, t.Version, t.Machine, t.Environment, t.LastHeartbeatUtc, t.Overall, t.ErrorTotal)));
}).WithName("GetTerminals").WithSummary("List known terminals with their latest state.");

app.Run();

public partial class Program;
