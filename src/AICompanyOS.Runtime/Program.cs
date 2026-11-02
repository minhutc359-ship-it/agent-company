using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AICompanyOS.Runtime;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 4173));
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

var dataRoot = Environment.GetEnvironmentVariable("AICOS_DATA_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AICompanyOS");
var sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
builder.Services.AddSingleton(_ => new FileEventStore(dataRoot));
builder.Services.AddSingleton(new RuntimeSession(sessionToken));

var app = builder.Build();
app.Use(async (context, next) =>
{
    if (context.Connection.RemoteIpAddress is not { } remoteAddress || !IPAddress.IsLoopback(remoteAddress) ||
        !string.Equals(context.Request.Host.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    if (context.Request.Path.StartsWithSegments("/api") && context.Request.Path != "/api/session")
    {
        var session = context.RequestServices.GetRequiredService<RuntimeSession>();
        if (!context.Request.Cookies.TryGetValue("aicos_session", out var cookie) || !session.IsValid(cookie))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (!string.Equals(origin, "http://127.0.0.1:4173", StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }
    }

    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; connect-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/session", (HttpContext context, RuntimeSession session) =>
{
    context.Response.Cookies.Append("aicos_session", session.Token, new CookieOptions
    {
        HttpOnly = true,
        Secure = false,
        SameSite = SameSiteMode.Strict,
        IsEssential = true,
        Path = "/"
    });
    return Results.Ok(new { authenticated = true });
});

app.MapGet("/api/v1/health", () => Results.Ok(new
{
    status = "local",
    runtime = ".NET 10",
    storage = "file-store",
    inference = "not-configured",
    outboundCalls = false
}));

app.MapPost("/api/v1/companies/{companyId}/workflow-runs", async (
    string companyId,
    CreateRunRequest request,
    HttpContext context,
    FileEventStore store,
    CancellationToken cancellationToken) =>
{
    if (!IsValidId(companyId) || string.IsNullOrWhiteSpace(request.Objective) || request.Objective.Length > 4000 ||
        request.SourceRefs is { Length: > 50 } || request.SourceRefs?.Any(reference => !IsValidSourceRef(reference)) == true ||
        !context.Request.Headers.TryGetValue("Idempotency-Key", out var keys) || !IsValidId(keys.ToString()))
        return Results.BadRequest(new { error = "Company ID or objective is invalid." });

    var runId = $"run-{Guid.NewGuid():N}";
    var result = await store.CreateRunAsync(companyId, runId, request.Objective.Trim(),
        request.SourceRefs?.Distinct().ToArray() ?? [], keys.ToString(), cancellationToken);
    if (result.Conflict) return Results.Conflict(new { error = "Idempotency-Key was already used for a different request." });
    return result.Created
        ? Results.Created($"/api/v1/companies/{companyId}/workflow-runs/{result.Run.RunId}", result.Run)
        : Results.Ok(result.Run);
});

app.MapGet("/api/v1/companies/{companyId}/workflow-runs", async (
    string companyId, FileEventStore store, CancellationToken cancellationToken) =>
{
    if (!IsValidId(companyId)) return Results.BadRequest(new { error = "Company ID is invalid." });
    return Results.Ok(await store.ListRunsAsync(companyId, cancellationToken));
});

app.MapGet("/api/v1/companies/{companyId}/workflow-runs/{runId}", async (
    string companyId, string runId, FileEventStore store, CancellationToken cancellationToken) =>
{
    if (!IsValidId(companyId) || !IsValidId(runId)) return Results.BadRequest();
    var run = await store.GetRunAsync(companyId, runId, cancellationToken);
    return run is null ? Results.NotFound() : Results.Ok(run);
});

app.MapGet("/api/v1/companies/{companyId}/events", async (
    string companyId, long? afterSeq, FileEventStore store, CancellationToken cancellationToken) =>
{
    if (!IsValidId(companyId)) return Results.BadRequest(new { error = "Company ID is invalid." });
    return Results.Ok(await store.ReadEventsAsync(companyId, Math.Max(0, afterSeq ?? 0), cancellationToken));
});

app.MapPost("/api/v1/companies/{companyId}/workflow-runs/{runId}/handoff/ack", async (
    string companyId,
    string runId,
    HandoffAckRequest request,
    HttpContext context,
    FileEventStore store,
    CancellationToken cancellationToken) =>
{
    if (!IsValidId(companyId) || !IsValidId(runId) || !IsValidId(request.ReceiverAgentId) ||
        !context.Request.Headers.TryGetValue("Idempotency-Key", out var keys) || !IsValidId(keys.ToString()))
        return Results.BadRequest(new { error = "Valid receiverAgentId and Idempotency-Key are required." });

    var result = await store.AcknowledgeHandoffAsync(
        companyId, runId, request.PacketHash, request.ReceiverAgentId, keys.ToString(), cancellationToken);
    return result.Outcome switch
    {
        CommandOutcome.NotFound => Results.NotFound(),
        CommandOutcome.Conflict => Results.Conflict(new { error = result.Message }),
        CommandOutcome.Invalid => Results.UnprocessableEntity(new { error = result.Message }),
        _ => Results.Ok(result.Run)
    };
});

app.MapPost("/api/v1/companies/{companyId}/workflow-runs/{runId}/approval", async (
    string companyId,
    string runId,
    OwnerApprovalRequest request,
    HttpContext context,
    FileEventStore store,
    CancellationToken cancellationToken) =>
{
    if (!IsValidId(companyId) || !IsValidId(runId) ||
        !context.Request.Headers.TryGetValue("Idempotency-Key", out var keys) || !IsValidId(keys.ToString()))
        return Results.BadRequest(new { error = "Idempotency-Key is required." });

    var result = await store.DecideApprovalAsync(companyId, runId, request.Approved, keys.ToString(), cancellationToken);
    return result.Outcome switch
    {
        CommandOutcome.NotFound => Results.NotFound(),
        CommandOutcome.Conflict => Results.Conflict(new { error = result.Message }),
        CommandOutcome.Invalid => Results.UnprocessableEntity(new { error = result.Message }),
        _ => Results.Ok(result.Run)
    };
});

app.MapGet("/api/v1/companies/{companyId}/workflow-runs/{runId}/report", async (
    string companyId, string runId, FileEventStore store, CancellationToken cancellationToken) =>
{
    if (!IsValidId(companyId) || !IsValidId(runId)) return Results.BadRequest();
    var run = await store.GetRunAsync(companyId, runId, cancellationToken);
    return run is null ? Results.NotFound() : Results.Ok(WorkflowReport.FromRun(run));
});

app.Run();

static bool IsValidId(string? value) =>
    value is { Length: > 0 and <= 100 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

static bool IsValidSourceRef(string? value) => value is { Length: > 0 and <= 240 } &&
    value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or ':' or '.' or '@' or '#' or '/');

public partial class Program;