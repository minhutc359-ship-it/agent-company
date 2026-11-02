using System.Net;
using System.Text;
using System.Text.Json;

namespace AICompanyOS.Runtime;

public sealed record SoftwareModelResult(JsonElement Output, ModelUsage Usage, string Sha256);
public interface ISoftwareModelClient
{
    Task<LocalModel[]> ListAsync(CancellationToken ct);
    Task<SoftwareModelResult> GenerateAsync(RoleBinding binding, SoftwareJob job, CancellationToken ct);
}

public sealed class OllamaSoftwareClient : ISoftwareModelClient, IDisposable
{
    private readonly OllamaProvider _catalog;
    private readonly HttpClient _http;
    public OllamaSoftwareClient(string endpoint, HttpMessageHandler? testHandler = null)
    {
        _catalog = new OllamaProvider(endpoint);
        _http = new HttpClient(testHandler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = new Uri(endpoint), Timeout = Timeout.InfiniteTimeSpan };
    }
    public void Dispose() { _catalog.Dispose(); _http.Dispose(); }
    public async Task<LocalModel[]> ListAsync(CancellationToken ct)
    {
        // Use the same locality/digest validation as the established catalog adapter.
        return await _catalog.ListAsync(ct);
    }
    public async Task<SoftwareModelResult> GenerateAsync(RoleBinding binding, SoftwareJob job, CancellationToken ct)
    {
        if (binding.Model is null || RuntimeJson.TextHash(job.Input) != job.InputSha256) throw new ModelProviderException("MODEL_ROUTE_OR_INPUT_UNVERIFIED");
        var catalog = await ListAsync(ct);
        if (!catalog.Any(m => m.Name == binding.Model.Name && m.Digest == binding.Model.Digest)) throw new ModelProviderException("MODEL_CHANGED_OR_UNAVAILABLE");
        using var metadata = await ReadAsync("api/show", JsonSerializer.Serialize(new { model = binding.Model.Name }, RuntimeJson.Options), 10, ct);
        var root = metadata.RootElement;
        if (root.TryGetProperty("remote_host", out var host) && host.ValueKind != JsonValueKind.Null && host.ToString().Length > 0 ||
            root.TryGetProperty("remote_model", out var remote) && remote.ValueKind != JsonValueKind.Null && remote.ToString().Length > 0 ||
            !root.TryGetProperty("details", out var details) || !details.TryGetProperty("format", out var format) || format.GetString() != "gguf" ||
            !root.TryGetProperty("capabilities", out var capabilities) || !capabilities.EnumerateArray().Any(c => c.GetString() == "completion") ||
            !root.TryGetProperty("model_info", out var info) || !info.EnumerateObject().Any(p => p.Name.EndsWith(".context_length", StringComparison.Ordinal) && p.Value.TryGetInt64(out var capacity) && capacity >= 16384))
            throw new ModelProviderException("MODEL_CAPABILITY_UNVERIFIED");
        using var input = JsonDocument.Parse(job.Input);
        if (input.RootElement.GetProperty("model").GetString() != binding.Model.Name || input.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32() != binding.Configuration.MaxOutputTokens || input.RootElement.TryGetProperty("tools", out _))
            throw new ModelProviderException("MODEL_PAYLOAD_MISMATCH");
        using var response = await ReadAsync("api/chat", job.Input, 90, ct); root = response.RootElement;
        if (!root.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True || root.GetProperty("done_reason").GetString() != "stop" || root.GetProperty("model").GetString() != binding.Model.Name ||
            !root.TryGetProperty("message", out var message) || message.GetProperty("role").GetString() != "assistant" ||
            message.TryGetProperty("tool_calls", out var calls) && (calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() > 0)) throw new ModelProviderException("MODEL_RESPONSE_REJECTED");
        int? Count(string field)
        {
            if (!root.TryGetProperty(field, out var value)) return null;
            if (!value.TryGetInt32(out var count) || count < 0) throw new ModelProviderException("MODEL_USAGE_INVALID"); return count;
        }
        var usage = new ModelUsage(Count("prompt_eval_count"), Count("eval_count"));
        if (usage.OutputTokens > binding.Configuration.MaxOutputTokens || usage.InputTokens > 16384 - binding.Configuration.MaxOutputTokens) throw new ModelProviderException("MODEL_TOKEN_BUDGET_EXCEEDED");
        var content = message.GetProperty("content").GetString();
        if (content is null || content.Length > 196608 || SourceSafety.ContainsKnownSecret(content)) throw new ModelProviderException("MODEL_OUTPUT_REJECTED");
        try
        {
            using var output = JsonDocument.Parse(content);
            return new(output.RootElement.Clone(), usage, RuntimeJson.Hash(output.RootElement));
        }
        catch (JsonException) { throw new ModelProviderException("MODEL_OUTPUT_SCHEMA_INVALID"); }
    }
    private async Task<JsonDocument> ReadAsync(string path, string payload, int seconds, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode != HttpStatusCode.OK) throw new ModelProviderException("LOCAL_PROVIDER_REQUEST_FAILED");
            if (response.Content.Headers.ContentLength > 256 * 1024) throw new ModelProviderException("MODEL_RESPONSE_TOO_LARGE");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token); using var bytes = new MemoryStream(); var buffer = new byte[4096]; int count;
            while ((count = await stream.ReadAsync(buffer, deadline.Token)) > 0) { if (bytes.Length + count > 256 * 1024) throw new ModelProviderException("MODEL_RESPONSE_TOO_LARGE"); bytes.Write(buffer, 0, count); }
            return JsonDocument.Parse(bytes.ToArray());
        }
        catch (HttpRequestException) { throw new ModelProviderException("LOCAL_PROVIDER_UNAVAILABLE"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ModelProviderException("LOCAL_PROVIDER_TIMEOUT"); }
        catch (JsonException) { throw new ModelProviderException("LOCAL_PROVIDER_JSON_INVALID"); }
    }
}

public sealed partial class FileEventStore
{
    public async Task<SoftwareJob> PrepareSoftwareModelCallAsync(SoftwareJob claim, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(claim.CompanyId, 0, ct); var run = RebuildSoftwareRun(claim.CompanyId, claim.RunId, events.Where(e => e.RunId == claim.RunId).ToArray())!;
            var job = run.Jobs.Single(j => j.JobId == claim.JobId);
            if (!run.HandoffAcks!.Any(a => a.JobId == job.JobId && a.InputSha256 == job.InputSha256 && a.LeaseToken == job.LeaseToken && a.ReceiverAgentId == job.AgentId && a.ReceiverRoleId == job.RoleId)) throw new ExecutionBlockedException("MODEL_CONTEXT_NOT_ACKNOWLEDGED");
            var budget = run.Jobs.Sum(j => j.ModelCalls * Binding(run, j.RoleId, j.AgentId).Configuration.MaxOutputTokens);
            if (run.State is "paused" or "cancelled" || job.Status != "running" || job.LeaseToken != claim.LeaseToken || job.LeaseUntil <= DateTimeOffset.UtcNow || job.ModelCalls >= 6 || DateTimeOffset.UtcNow - run.Seed.CreatedAt > TimeSpan.FromMinutes(run.Seed.Workflow.MaxElapsedMinutes) ||
                budget + Binding(run, job.RoleId, job.AgentId).Configuration.MaxOutputTokens > run.Seed.Workflow.MaxOutputTokens) throw new ExecutionBlockedException("MODEL_CALL_OR_RUN_BUDGET_EXHAUSTED");
            var prepared = job with { ModelCalls = job.ModelCalls + 1, UnreportedCalls = job.UnreportedCalls + 1 };
            await AppendUnsafeAsync(claim.CompanyId, claim.RunId, [DomainEvent.Create("software.job.changed", prepared), DomainEvent.Create("software.model.call.prepared", new { job.JobId, prepared.ModelCalls, job.InputSha256 })], events, ct); return prepared;
        }
        finally { _gate.Release(); }
    }
    public async Task<SoftwareJob> RecordSoftwareModelCallAsync(SoftwareJob claim, SoftwareModelResult result, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(claim.CompanyId, 0, ct); var run = RebuildSoftwareRun(claim.CompanyId, claim.RunId, events.Where(e => e.RunId == claim.RunId).ToArray())!; var job = run.Jobs.Single(j => j.JobId == claim.JobId);
            if (run.State is "paused" or "cancelled" || job.Status != "running" || job.LeaseToken != claim.LeaseToken || job.ModelCalls != claim.ModelCalls || job.InputSha256 != claim.InputSha256) throw new ExecutionBlockedException("MODEL_CALL_LEASE_REPLACED");
            var updated = job with { InputTokens = job.UnreportedCalls > 1 ? null : job.ModelCalls == 1 ? result.Usage.InputTokens : job.InputTokens is not null && result.Usage.InputTokens is not null ? job.InputTokens + result.Usage.InputTokens : null,
                OutputTokens = job.UnreportedCalls > 1 ? null : job.ModelCalls == 1 ? result.Usage.OutputTokens : job.OutputTokens is not null && result.Usage.OutputTokens is not null ? job.OutputTokens + result.Usage.OutputTokens : null,
                UnreportedCalls = job.UnreportedCalls - (result.Usage.InputTokens is not null && result.Usage.OutputTokens is not null ? 1 : 0) };
            await AppendUnsafeAsync(claim.CompanyId, claim.RunId, [DomainEvent.Create("software.job.changed", updated), DomainEvent.Create("software.model.call.recorded", new { job.JobId, job.ModelCalls, job.InputSha256, result.Usage, result.Sha256 })], events, ct); return updated;
        }
        finally { _gate.Release(); }
    }
    public async Task<SoftwareJob> ExpandSoftwareContextAsync(SoftwareJob claim, string[] paths, CancellationToken ct)
    {
        if (paths is not { Length: > 0 and <= 6 } || paths.Any(p => !SoftwareContracts.SafeRelative(p))) throw new ArgumentException("Request 1–6 pinned files.");
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(claim.CompanyId, 0, ct); var run = RebuildSoftwareRun(claim.CompanyId, claim.RunId, events.Where(e => e.RunId == claim.RunId).ToArray())!; var job = run.Jobs.Single(j => j.JobId == claim.JobId);
            if (job.TaskId is null || run.State is "paused" or "cancelled" || job.Status != "running" || job.LeaseToken != claim.LeaseToken || job.LeaseUntil <= DateTimeOffset.UtcNow) throw new ExecutionBlockedException("CONTEXT_READ_NOT_AUTHORIZED");
            var input = SoftwareContext.Build(run, job.RoleId, job.TaskId, job.NodeId, paths);
            var cloud = Binding(run, job.RoleId, job.AgentId).Cloud is not null;
            var updated = job with { Input = input, InputSha256 = RuntimeJson.TextHash(input), Status = cloud ? "waiting_cloud_approval" : job.Status, CloudApprovalSha256 = null, LeaseToken = cloud ? null : job.LeaseToken, LeaseUntil = cloud ? null : job.LeaseUntil };
            var packet = BuildSoftwareHandoff(run, updated);
            var updatedRun = run with { Handoffs = [.. run.Handoffs ?? [], packet] };
            await AppendUnsafeAsync(claim.CompanyId, claim.RunId, new[] { DomainEvent.Create("software.handoff.created", packet) }.Concat(cloud ? Array.Empty<DomainEvent>() : new[] { AcknowledgeSoftwareContext(updatedRun, updated) }).Concat(new[] { NodeEvent(job.NodeId, updated.Status), DomainEvent.Create("software.job.changed", updated), DomainEvent.Create("software.context.read", new { job.JobId, paths, updated.InputSha256, snapshot = run.Seed.Repository.Sha256 }) }).ToArray(), events, ct); return updated;
        }
        finally { _gate.Release(); }
    }
}
