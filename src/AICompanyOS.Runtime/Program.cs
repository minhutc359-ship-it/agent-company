using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AICompanyOS.Runtime;
using Microsoft.AspNetCore.RateLimiting;

const string origin = "http://127.0.0.1:4173";
var dataRoot = Environment.GetEnvironmentVariable("AICOS_DATA_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AICompanyOS");

if (args is ["--restore-backup", var backupPath])
{
    Console.Write("Backup passphrase: ");
    var passphrase = ReadPassphrase();
    using var restore = new FileEventStore(dataRoot);
    var result = await restore.RestoreIntoEmptyAsync(await File.ReadAllBytesAsync(backupPath), passphrase, CancellationToken.None);
    Console.WriteLine($"Restored {result.EventCount} events; checksum {result.Digest}.");
    return;
}
if (args.Length >= 1 && args[0] == "--initialize-local")
{
    var model = args.Length == 3 && args[1] == "--model" ? args[2] : null;
    if (args.Length is not (1 or 3) || args.Length == 3 && args[1] != "--model") throw new ArgumentException("Use --initialize-local [--model installed-model].");
    using var store = new FileEventStore(dataRoot); await store.InitializeAsync(CancellationToken.None);
    var manifest = await store.VerifyAsync(CancellationToken.None);
    if (manifest.Backend != "sqlite") await store.MigrateAsync("sqlite", manifest.Digest, CancellationToken.None);
    var catalog = model is null ? [] : await new OllamaSoftwareClient(Environment.GetEnvironmentVariable("AICOS_OLLAMA_URL") ?? "http://127.0.0.1:11434/").ListAsync(CancellationToken.None);
    var demo = await new LocalSetupService(dataRoot).InstallDemoAsync(store, new RepositoryService(dataRoot), model, catalog, CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(demo, RuntimeJson.Options)); return;
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, 4173);
    options.Limits.MaxRequestBodySize = 180 * 1024 * 1024;
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    // Realtime snapshots must not consume the Owner's mutation allowance.
    options.AddPolicy("local", context =>
    {
        var reading = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);
        return RateLimitPartition.GetFixedWindowLimiter(reading ? "read" : "write", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = reading ? 1200 : 240, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true });
    });
    options.AddConcurrencyLimiter("backup", limit => { limit.PermitLimit = 1; limit.QueueLimit = 0; });
});
builder.Services.AddSingleton(_ => new FileEventStore(dataRoot));
builder.Services.AddSingleton<ILocalModelProvider>(_ => new OllamaProvider(Environment.GetEnvironmentVariable("AICOS_OLLAMA_URL") ?? "http://127.0.0.1:11434/"));
builder.Services.AddHostedService<InferenceWorker>();
builder.Services.AddSingleton(new RepositoryService(dataRoot));
builder.Services.AddSingleton(new LocalSetupService(dataRoot));
builder.Services.AddSingleton(new DockerSandbox(dataRoot));
builder.Services.AddSingleton(_ => new OllamaSoftwareClient(Environment.GetEnvironmentVariable("AICOS_OLLAMA_URL") ?? "http://127.0.0.1:11434/"));
builder.Services.AddSingleton(new ClaudeCodeClient(dataRoot));
builder.Services.AddSingleton(_ => new CloudProviderClient(maxPayloadBytes: 64000));
builder.Services.AddSingleton<ISoftwareModelClient>(sp => new RoutedSoftwareClient(sp.GetRequiredService<OllamaSoftwareClient>(), sp.GetRequiredService<FileEventStore>(), sp.GetRequiredService<RuntimeCredentialVault>(), sp.GetRequiredService<CloudProviderClient>(), sp.GetRequiredService<ClaudeCodeClient>()));
builder.Services.AddSingleton(new UpdateService(dataRoot));
builder.Services.AddSingleton<SoftwareCoordinator>();
builder.Services.AddSingleton(provider => new SoftwareReleaseService(provider.GetRequiredService<FileEventStore>(), provider.GetRequiredService<RepositoryService>(), dataRoot));
builder.Services.AddHostedService<SoftwareWorker>();
builder.Services.AddHostedService<CollaborationWorker>();
builder.Services.AddSingleton<RuntimeCredentialVault>();
builder.Services.AddSingleton<ICloudProviderClient, CloudProviderClient>();
builder.Services.AddHostedService<CloudEgressWorker>();
builder.Services.AddSingleton(new RuntimeSession(Convert.ToHexString(RandomNumberGenerator.GetBytes(32))));

var app = builder.Build();
await app.Services.GetRequiredService<FileEventStore>().InitializeAsync(CancellationToken.None);
app.Use(async (context, next) =>
{
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; connect-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Cache-Control"] = "no-store";
    context.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
    if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
        context.Request.Host.Value != "127.0.0.1:4173")
    {
        await Error(context, 403, "LOCALHOST_REQUIRED", "Use the local runtime address.");
        return;
    }
    var isApi = context.Request.Path.StartsWithSegments("/api");
    if (isApi && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && context.RequestServices.GetRequiredService<FileEventStore>().Maintenance)
    { await Error(context, 409, "UPDATE_IN_PROGRESS", "Ứng dụng đang sao lưu và nâng cấp. Vui lòng chờ khởi động lại."); return; }
    if (isApi)
    {
        var requestOrigin = context.Request.Headers.Origin.ToString();
        if ((requestOrigin.Length > 0 && requestOrigin != origin) || context.Request.Headers["Sec-Fetch-Site"] == "cross-site")
        {
            await Error(context, 403, "ORIGIN_DENIED", "A same-origin request is required.");
            return;
        }
        if (context.Request.Path != "/api/session")
        {
            var session = context.RequestServices.GetRequiredService<RuntimeSession>();
            if (!context.Request.Cookies.TryGetValue("aicos_session", out var cookie) || !session.IsValid(cookie))
            {
                await Error(context, 401, "SESSION_REQUIRED", "Open the workspace to establish a local session.");
                return;
            }
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && requestOrigin != origin)
            {
                await Error(context, 403, "ORIGIN_REQUIRED", "Writes require the exact local Origin header.");
                return;
            }
        }
    }
    try { await next(); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception error) when (!context.Response.HasStarted)
    {
        var (status, code, message) = error switch
        {
            ModelProviderException providerError => (502, providerError.Code, "Ollama is unavailable or the selected local model failed its capability checks. Check the local server and installed model."),
            CloudProviderException cloudError => (502, cloudError.Code, "The approved cloud request failed or its response did not satisfy the pinned contract. Inspect the egress record before retrying."),
            ExecutionBlockedException blocked => (409, blocked.Message, "Execution was blocked. Check the pinned repository, Docker Linux engine/image and recorded scope; no host execution fallback is allowed."),
            WorkspaceConflictException => (409, "VERSION_CONFLICT", error.Message),
            KeyNotFoundException => (404, "NOT_FOUND", "The requested record does not exist in this company."),
            ArgumentException => (400, "INVALID_REQUEST", error.Message),
            BadHttpRequestException => (400, "INVALID_REQUEST", "The request body is invalid or too large."),
            InvalidDataException => (503, "INTEGRITY_FAILURE", "Storage or backup verification failed. Original data was retained; inspect the local workspace."),
            _ => (500, "RUNTIME_ERROR", "The local operation failed. No completion is implied; reload its state before retrying.")
        };
        await Error(context, status, code, message);
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();

app.MapGet("/api/session", (HttpContext context, RuntimeSession session) =>
{
    context.Response.Cookies.Append("aicos_session", session.Token, new CookieOptions
    { HttpOnly = true, Secure = false, SameSite = SameSiteMode.Strict, IsEssential = true, Path = "/" });
    return Results.Ok(new { authenticated = true });
}).RequireRateLimiting("local");

var api = app.MapGroup("/api/v1").RequireRateLimiting("local");
api.MapGet("/health", (FileEventStore store) => Results.Ok(new
{ status = "local", version = "2.5.0", build = RuntimeBuild.Info, runtime = ".NET 10", storage = store.Backend, inference = "local-ollama-or-controlled-cloud", externalCalls = "exact-preview-owner-approved-only", network = "loopback-models; network-none-containers; allowlisted-cloud-per-manifest; opt-in-github-release", milestone = "company-os-2.5-guided-workspace", deliveryWorkflow = DeliveryTools.WorkflowVersion, softwareWorkflow = SoftwareContracts.DefaultWorkflow.Version }));
api.MapGet("/providers/local/models", async (ILocalModelProvider provider, CancellationToken ct) => Results.Ok(await provider.ListAsync(ct)));
api.MapGet("/roles", () => Results.Ok(BuiltInCatalog.Roles));
api.MapGet("/skills", () => Results.Ok(BuiltInCatalog.Skills));
api.MapGet("/companies", async (FileEventStore store, CancellationToken ct) => Results.Ok(await store.ListCompaniesAsync(ct)));
api.MapPost("/companies", async (CreateCompanyRequest request, FileEventStore store, CancellationToken ct) => Results.Ok(await store.CreateCompanyAsync(request, ct)));

var companies = api.MapGroup("/companies/{companyId}").AddEndpointFilter(async (context, next) =>
{
    foreach (var name in new[] { "companyId", "runId", "agentId", "sourceId", "proposalId", "snapshotId" })
        if (context.HttpContext.Request.RouteValues.TryGetValue(name, out var value) && !Identifiers.IsValid(value?.ToString()))
            return Results.BadRequest(new { error = new { code = "INVALID_ID", message = "Invalid record identifier." } });
    var store = context.HttpContext.RequestServices.GetRequiredService<FileEventStore>();
    var company = context.HttpContext.Request.RouteValues["companyId"]!.ToString()!;
    if (!await store.CompanyExistsAsync(company, context.HttpContext.RequestAborted)) return Results.NotFound();
    return await next(context);
});
companies.MapGet("/agents", async (string companyId, FileEventStore store, CancellationToken ct) => Results.Ok(await store.ListAgentsAsync(companyId, ct)));
companies.MapPost("/agents", async (string companyId, CreateAgentRequest request, FileEventStore store, CancellationToken ct) => Results.Ok(await store.CreateAgentAsync(companyId, request, ct)));
companies.MapPut("/agents/{agentId}/roles", async (string companyId, string agentId, AssignRolesRequest request, FileEventStore store, CancellationToken ct) =>
    Results.Ok(await store.AssignRolesAsync(companyId, agentId, request, ct)));
companies.MapGet("/workspace/snapshot", async (string companyId, FileEventStore store, CancellationToken ct) => Results.Ok(await store.WorkspaceSnapshotAsync(companyId, ct)));
companies.MapGet("/office/snapshot", async (string companyId, FileEventStore store, CancellationToken ct) => Results.Ok(await store.OfficeSnapshotAsync(companyId, ct)));
companies.MapGet("/workflow-runs", async (string companyId, FileEventStore store, CancellationToken ct) => Results.Ok(await store.ListRunsAsync(companyId, ct)));
companies.MapPost("/workflow-runs", async Task<IResult> (string companyId, CreateRunRequest request, HttpContext context, FileEventStore store, ILocalModelProvider provider, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Objective) || request.Objective.Length > 4000 || SourceSafety.ContainsKnownSecret(request.Objective) ||
        request.SourceRefs is { Length: > 50 } || request.SourceRefs?.Any(reference => !IsValidSourceRef(reference)) == true ||
        request.ExecutionMode is not ("scaffold" or "local_model") || (request.ExecutionMode == "local_model" ? !OllamaProvider.ValidModelName(request.ModelId) : request.ModelId is not null) ||
        !Identifiers.IsValid(request.LeaderAgentId ?? "leader-agent") || !Identifiers.IsValid(request.BaAgentId ?? "ba-agent"))
        throw new ArgumentException("A valid objective, source reference labels and assigned agent IDs are required.");
    CreateRunResult result;
    var refs = request.SourceRefs?.Distinct().ToArray() ?? [];
    var key = CommandKey(context);
    if (request.ExecutionMode == "local_model")
    {
        var replay = await store.ReplayLocalCreateAsync(companyId, request.Objective.Trim(), refs, request.ModelId!, key,
            request.BaAgentId ?? "ba-agent", request.LeaderAgentId ?? "leader-agent", ct);
        if (replay is not null) result = replay;
        else
        {
            var model = (await provider.ListAsync(ct)).FirstOrDefault(m => m.Name == request.ModelId) ?? throw new ArgumentException("Select an installed local GGUF model.");
            result = await store.CreateLocalRunAsync(companyId, "run-" + Guid.NewGuid().ToString("N"), request.Objective.Trim(), refs, model, key, ct,
                request.BaAgentId ?? "ba-agent", request.LeaderAgentId ?? "leader-agent");
        }
    }
    else result = await store.CreateRunAsync(companyId, "run-" + Guid.NewGuid().ToString("N"), request.Objective.Trim(), refs, key, ct,
        request.LeaderAgentId ?? "leader-agent", request.BaAgentId ?? "ba-agent");
    if (result.Conflict) throw new WorkspaceConflictException("Idempotency-Key belongs to a different request.");
    return result.Created ? Results.Created($"/api/v1/companies/{companyId}/workflow-runs/{result.Run.RunId}", result.Run) : Results.Ok(result.Run);
});
companies.MapGet("/workflow-runs/{runId}", async Task<IResult> (string companyId, string runId, FileEventStore store, CancellationToken ct) =>
{
    var run = await store.GetRunAsync(companyId, runId, ct);
    return run is null ? Results.NotFound() : Results.Ok(run);
});
companies.MapPost("/workflow-runs/{runId}/handoff/ack", async (string companyId, string runId, HandoffAckRequest request, HttpContext context, FileEventStore store, CancellationToken ct) =>
{
    if (!Identifiers.IsValid(request.ReceiverAgentId) || request.PacketHash is not { Length: > 0 and <= 100 }) throw new ArgumentException("A valid receiver and packet digest are required.");
    return CommandResponse(await store.AcknowledgeHandoffAsync(companyId, runId, request.PacketHash, request.ReceiverAgentId, CommandKey(context), ct, request.ExpectedSequence));
});
companies.MapPost("/workflow-runs/{runId}/approval", async (string companyId, string runId, OwnerApprovalRequest request, HttpContext context, FileEventStore store, CancellationToken ct) =>
    CommandResponse(await store.DecideApprovalAsync(companyId, runId, request.Approved, CommandKey(context), ct, request.ExpectedSequence)));
foreach (var action in new[] { "pause", "resume", "cancel" })
    companies.MapPost($"/workflow-runs/{{runId}}/{action}", async (string companyId, string runId, RunControlRequest request, HttpContext context, FileEventStore store, CancellationToken ct) =>
        CommandResponse(await store.ControlRunAsync(companyId, runId, action, request.ExpectedSequence, CommandKey(context), ct)));
companies.MapPost("/workflow-runs/{runId}/retry", async (string companyId, string runId, RunControlRequest request, HttpContext context, FileEventStore store, CancellationToken ct) =>
    CommandResponse(await store.RetryModelJobAsync(companyId, runId, request.ExpectedSequence, CommandKey(context), ct)));
companies.MapGet("/workflow-runs/{runId}/report", async Task<IResult> (string companyId, string runId, FileEventStore store, CancellationToken ct) =>
{
    var run = await store.GetRunAsync(companyId, runId, ct);
    return run is null ? Results.NotFound() : Results.Ok(WorkflowReport.FromRun(run));
});
companies.MapPost("/workflow-runs/{runId}/delivery/prepare", async (string companyId, string runId, DeliveryPrepareRequest request, HttpContext context, FileEventStore store, CancellationToken ct) =>
    CommandResponse(await store.PrepareDeliveryAsync(companyId, runId, request, CommandKey(context), ct)));
companies.MapPost("/workflow-runs/{runId}/delivery/artifacts", async (string companyId, string runId, ArtifactSubmitRequest request, HttpContext context, FileEventStore store, CancellationToken ct) =>
    CommandResponse(await store.SubmitArtifactAsync(companyId, runId, request, CommandKey(context), ct)));
companies.MapPost("/workflow-runs/{runId}/delivery/review", async (string companyId, string runId, DeliveryReviewRequest request, HttpContext context, FileEventStore store, CancellationToken ct) =>
    CommandResponse(await store.ReviewDeliveryAsync(companyId, runId, request, CommandKey(context), ct)));
companies.MapGet("/workflow-runs/{runId}/delivery/preview", async Task<IResult> (string companyId, string runId, HttpContext context, FileEventStore store, CancellationToken ct) =>
{
    var run = await store.GetRunAsync(companyId, runId, ct);
    if (run?.Delivery is null) return Results.NotFound();
    if (run.State is "paused" or "cancelled") throw new WorkspaceConflictException("Preview is unavailable while paused or cancelled.");
    context.Response.Headers.ContentSecurityPolicy = DeliveryTools.PreviewPolicy;
    return Results.Content(DeliveryTools.Preview(run.Delivery), "text/html; charset=utf-8");
});
companies.MapGet("/workflow-runs/{runId}/delivery/export", async Task<IResult> (string companyId, string runId, FileEventStore store, CancellationToken ct) =>
{
    var run = await store.GetRunAsync(companyId, runId, ct);
    return run is null ? Results.NotFound() : Results.File(DeliveryTools.Export(run), "application/zip", runId + "-delivery.zip");
});
companies.MapGet("/context/{snapshotId}", async Task<IResult> (string companyId, string snapshotId, FileEventStore store, CancellationToken ct) =>
{
    var runs = await store.ListRunsAsync(companyId, ct);
    var run = runs.FirstOrDefault(r => r.Handoff?.ContextSnapshotId == snapshotId);
    return run is null ? Results.NotFound() : Results.Ok(new
    {
        snapshotId, spec = run.Spec, specSha256 = run.Handoff!.SpecSha256,
        packet = run.Handoff, permissions = Array.Empty<string>(), inference = run.ExecutionMode, invocations = run.Invocations,
        warning = "Source reference labels are not verified tool evidence. Canonical records are pinned to this run; later edits do not change its context."
    });
});
companies.MapGet("/events", async (string companyId, long? afterSeq, FileEventStore store, CancellationToken ct) =>
{
    if (afterSeq < 0) throw new ArgumentException("Event cursor must be non-negative.");
    return Results.Ok(await store.ReadEventsAsync(companyId, afterSeq ?? 0, ct));
});
companies.MapGet("/events/stream", async (string companyId, long? afterSeq, HttpContext context, FileEventStore store, CancellationToken ct) =>
{
    var cursor = afterSeq ?? 0;
    var lastId = context.Request.Headers["Last-Event-ID"].ToString();
    if (lastId.Length > 0 && !long.TryParse(lastId, out cursor) || cursor < 0) throw new ArgumentException("Invalid event cursor.");
    var initial = await store.ReadEventsAsync(companyId, 0, ct);
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers["X-Accel-Buffering"] = "no";
    if (cursor > (initial.LastOrDefault()?.Seq ?? 0))
    {
        await context.Response.WriteAsync($"event: reset\ndata: {{\"cursor\":0}}\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
        return;
    }
    var heartbeatAt = DateTimeOffset.UtcNow;
    await context.Response.WriteAsync(": connected\n\n", ct);
    await context.Response.Body.FlushAsync(ct);
    while (!ct.IsCancellationRequested)
    {
        var batch = await store.ReadEventsAsync(companyId, cursor, ct);
        foreach (var item in batch)
        {
            await context.Response.WriteAsync($"id: {item.Seq}\nevent: workspace\ndata: {JsonSerializer.Serialize(item, RuntimeJson.Options)}\n\n", ct);
            cursor = item.Seq;
        }
        if (DateTimeOffset.UtcNow - heartbeatAt > TimeSpan.FromSeconds(15))
        {
            await context.Response.WriteAsync(": heartbeat\n\n", ct);
            heartbeatAt = DateTimeOffset.UtcNow;
        }
        await context.Response.Body.FlushAsync(ct);
        await Task.Delay(750, ct);
    }
});

companies.MapGet("/sources", async (string companyId, FileEventStore store, CancellationToken ct) => Results.Ok((await store.ListSourcesAsync(companyId, ct))
    .Select(s => new { s.SourceId, s.Revision, s.Title, s.ContentSha256, s.ImportedAt, s.Kind, s.Classification, s.ParserVersion, s.OriginalName, s.OriginalSha256, characters = s.Content.Length })));
companies.MapPost("/sources", async (string companyId, ImportSourceRequest request, FileEventStore store, CancellationToken ct) => Results.Ok(await store.ImportSourceAsync(companyId, request, ct)));
companies.MapPost("/sources/file", async (string companyId, HttpContext context, FileEventStore store, CancellationToken ct) =>
{
    var form = await context.Request.ReadFormAsync(ct);
    var file = form.Files.Count == 1 ? form.Files[0] : throw new ArgumentException("Upload exactly one document.");
    if (file.Length is <= 0 or > DocumentIngestion.MaxUploadBytes) throw new ArgumentException("Document must contain 1 byte to 8 MiB.");
    await using var input = file.OpenReadStream();
    using var output = new MemoryStream(); await input.CopyToAsync(output, ct);
    var parsed = DocumentIngestion.Parse(file.FileName, file.ContentType, output.ToArray(), form["title"].ToString());
    var sourceId = form["sourceId"].ToString();
    if (!int.TryParse(form["expectedRevision"].ToString(), out var expectedRevision) || expectedRevision < 0) throw new ArgumentException("Invalid source revision.");
    var classification = form["classification"].ToString();
    return Results.Ok(await store.ImportSourceAsync(companyId, new(sourceId, parsed.Title, parsed.Content, expectedRevision, parsed.Kind,
        classification, parsed.ParserVersion, parsed.OriginalName, parsed.OriginalSha256), ct));
});
companies.MapGet("/sources/{sourceId}/revisions/{revision:int}", async Task<IResult> (string companyId, string sourceId, int revision, FileEventStore store, CancellationToken ct) =>
{
    var source = await store.GetSourceAsync(companyId, sourceId, revision, ct);
    return source is null ? Results.NotFound() : Results.Ok(source);
});
companies.MapGet("/knowledge/search", async (string companyId, string q, FileEventStore store, CancellationToken ct) => Results.Ok(await store.SearchAsync(companyId, q, ct)));
companies.MapGet("/knowledge/index-status", async (string companyId, FileEventStore store, CancellationToken ct) => Results.Ok(await store.KnowledgeIndexStatusAsync(companyId, ct)));
companies.MapGet("/memory/proposals", async (string companyId, FileEventStore store, CancellationToken ct) => Results.Ok(await store.ListProposalsAsync(companyId, ct)));
companies.MapPost("/memory/proposals", async (string companyId, MemoryProposalRequest request, FileEventStore store, CancellationToken ct) => Results.Ok(await store.ProposeMemoryAsync(companyId, request, ct)));
companies.MapPost("/memory/proposals/{proposalId}/review", async (string companyId, string proposalId, MemoryReviewRequest request, FileEventStore store, CancellationToken ct) => Results.Ok(await store.ReviewMemoryAsync(companyId, proposalId, request, ct)));
companies.MapGet("/memory/canonical", async (string companyId, FileEventStore store, CancellationToken ct) => Results.Ok(await store.ListCanonicalAsync(companyId, ct)));
api.MapGet("/storage/verify", async (FileEventStore store, CancellationToken ct) => Results.Ok(await store.VerifyAsync(ct)));
api.MapPost("/storage/migrate", async (MigrationRequest request, FileEventStore store, CancellationToken ct) => Results.Ok(await store.MigrateAsync(request.Backend, request.ExpectedDigest, ct)));
api.MapPost("/backups/export", async (BackupRequest request, FileEventStore store, CancellationToken ct) =>
    Results.File(await store.ExportBackupAsync(request.Passphrase, ct), "application/octet-stream", $"ai-company-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.aicos")).RequireRateLimiting("backup");
api.MapPost("/backups/validate", (ValidateBackupRequest request) =>
{
    if (request.ArchiveBase64 is null || request.ArchiveBase64.Length > (BackupCodec.MaxArchiveBytes * 4L / 3) + 8) throw new ArgumentException("Backup exceeds the size limit.");
    byte[] bytes;
    try { bytes = Convert.FromBase64String(request.ArchiveBase64); }
    catch (FormatException) { throw new ArgumentException("Invalid backup encoding."); }
    try
    {
        var bundle = BackupCodec.Decrypt(bytes, request.Passphrase);
        return Results.Ok(new BackupValidation(bundle.Companies.Count, bundle.Companies.Values.Sum(e => e.Length), bundle.Digest));
    }
    catch (InvalidDataException) { return Results.BadRequest(new { error = new { code = "INVALID_BACKUP", message = "The backup could not be authenticated or decoded." } }); }
}).RequireRateLimiting("backup");
SoftwareEndpoints.Map(companies, api);
app.Run();

static string CommandKey(HttpContext context)
{
    var key = context.Request.Headers["Idempotency-Key"].ToString();
    if (!Identifiers.IsValid(key)) throw new ArgumentException("A valid Idempotency-Key is required.");
    return key;
}
static IResult CommandResponse(CommandResult result) => result.Outcome switch
{
    CommandOutcome.NotFound => Results.NotFound(),
    CommandOutcome.Conflict => Results.Conflict(new { error = new { code = "COMMAND_CONFLICT", message = result.Message } }),
    CommandOutcome.Invalid => Results.UnprocessableEntity(new { error = new { code = "INVALID_COMMAND", message = result.Message } }),
    _ => Results.Ok(result.Run)
};
static Task Error(HttpContext context, int status, string code, string message)
{
    context.Response.StatusCode = status;
    return context.Response.WriteAsJsonAsync(new { error = new { code, message, correlationId = context.TraceIdentifier } });
}
static bool IsValidSourceRef(string? value) => value is { Length: > 0 and <= 240 } &&
    value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.' or '@' or '#' or '/');
static string ReadPassphrase()
{
    if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
    var chars = new List<char>();
    while (true)
    {
        var key = Console.ReadKey(true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return new string(chars.ToArray()); }
        if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); }
        else if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
    }
}
public partial class Program;
