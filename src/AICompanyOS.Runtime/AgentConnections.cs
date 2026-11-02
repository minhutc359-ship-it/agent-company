using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AICompanyOS.Runtime;

public sealed record AgentCloudRoute(CloudProviderSnapshot Provider, string Engine, string? Executable = null, string? ExecutableSha256 = null, string? EntryPoint = null, string? EntryPointSha256 = null);
public sealed record AgentConnectRequest(int ExpectedVersion, string ProviderType, string ModelId, string Secret, bool AllowCloud, string Engine = "api");
public sealed record AgentModelRequest(string ProviderType, string Secret);
public sealed record CloudJobDecision(long ExpectedSequence, string PayloadSha256);
public sealed class CloudApprovalPendingException : Exception;

public static class SoftwareCloudPayload
{
    private static JsonElement StrictSchema(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText())!;
        void Visit(JsonNode? item)
        {
            if (item is JsonObject obj)
            {
                if (obj["properties"] is JsonObject properties)
                {
                    var required = (obj["required"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToHashSet() ?? [];
                    foreach (var pair in properties.ToArray())
                    {
                        if (!required.Contains(pair.Key)) properties[pair.Key] = new JsonObject { ["anyOf"] = new JsonArray(pair.Value!.DeepClone(), new JsonObject { ["type"] = "null" }) };
                    }
                    obj["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray()); obj["additionalProperties"] = false;
                }
                foreach (var pair in obj.ToArray()) Visit(pair.Value);
            }
            else if (item is JsonArray array) foreach (var child in array) Visit(child);
        }
        Visit(node); return JsonSerializer.SerializeToElement(node);
    }
    public static string Build(RoleBinding binding, SoftwareJob job)
    {
        using var input = JsonDocument.Parse(job.Input); var root = input.RootElement;
        var p = binding.Cloud!.Provider; var messages = root.GetProperty("messages"); var schema = root.GetProperty("format");
        var instruction = messages[0].GetProperty("content").GetString() + "\nReturn only one JSON object matching this schema. No tools, markdown or claims of execution.\n" + schema.GetRawText();
        var context = messages[1].GetProperty("content").GetString(); var limit = binding.Configuration.MaxOutputTokens;
        object payload = binding.Cloud.Engine == "claude-code" ? new { model = p.ModelId, instruction, context, schema, maxOutputTokens = limit, tools = Array.Empty<string>(), maxTurns = 2, maxBudgetUsd = .50 } : p.ProviderType switch
        {
            "openai" => (object)new { model = p.ModelId, instructions = instruction, input = context, store = false, max_output_tokens = limit, text = new { format = new { type = "json_schema", name = "software_artifact", strict = true, schema = StrictSchema(schema) } } },
            "anthropic" => new { model = p.ModelId, system = instruction, messages = new[] { new { role = "user", content = context } }, max_tokens = limit },
            "gemini" => new { model = p.ModelId, input = instruction + "\n" + context, generation_config = new { max_output_tokens = limit, thinking_summaries = "none", tool_choice = "none" }, store = false, background = false, stream = false },
            _ => throw new ArgumentException("Provider không hỗ trợ.")
        };
        return JsonSerializer.Serialize(payload, SoftwareJson.Compact);
    }
}

public sealed partial class FileEventStore
{
    private static AgentCloudRoute ResolveAgentCloud(AgentConfiguration configuration, IReadOnlyList<DomainEvent> events)
    {
        if (ProfileFrom(events).PrivacyMode != "controlled_cloud") throw new WorkspaceConflictException("Kết nối AI cloud cần bạn cho phép trong bước kết nối agent.");
        var p = CloudProvidersFrom(events).SingleOrDefault(p => p.ProviderId == configuration.ProviderId && p.Enabled && p.ModelId == configuration.ModelId) ?? throw new WorkspaceConflictException("Provider của agent đã thay đổi; kết nối lại trước khi giao việc.");
        var snapshot = new CloudProviderSnapshot(p.ProviderId, p.Version, p.ProviderType, p.ModelId, p.EndpointHost, p.EndpointPath, p.ApiVersion, p.CredentialRef, p.RetentionCaveat);
        return configuration.Engine == "claude-code" ? ClaudeCodeClient.Route(snapshot) : new(snapshot, "api");
    }
    private static void ValidateCloudCurrent(RoleBinding binding, SoftwareJob job, IReadOnlyList<DomainEvent> events, bool approved)
    {
        var route = binding.Cloud ?? throw new WorkspaceConflictException("Node không dùng cloud."); var p = route.Provider;
        var profile = ProfileFrom(events);
        if (binding.Configuration.MaxOutputTokens > profile.MaxCloudOutputTokens || SourceSafety.ContainsKnownSecret(job.Input)) throw new WorkspaceConflictException("Nội dung hoặc ngân sách chưa phù hợp policy cloud.");
        var run = RebuildSoftwareRun(job.CompanyId, job.RunId, events.Where(e => e.RunId == job.RunId).ToArray())!;
        var restricted = SourcesFrom(events).Where(s => s.Classification == "restricted").ToArray();
        if (run.Seed.Sources.Any(s => restricted.Any(r => r.SourceId == s.SourceId && r.Revision == s.Revision)) || run.Seed.Memory.Any(m => m.Citations.Any(c => restricted.Any(r => r.SourceId == c.SourceId && r.Revision == c.Revision)))) throw new WorkspaceConflictException("Tài liệu Restricted không được gửi tới AI cloud. Tạo run dùng nguồn được phép hoặc chọn Ollama local.");
        if (profile.PrivacyMode != "controlled_cloud" || !CloudProvidersFrom(events).Any(c => c.ProviderId == p.ProviderId && c.Enabled && c.Version == p.Version && c.ModelId == p.ModelId && c.ProviderType == p.ProviderType)) throw new WorkspaceConflictException("Policy hoặc provider đã thay đổi; tạo công việc mới với cấu hình đã kiểm tra.");
        if (approved && (job.CloudPolicyVersion != profile.CloudPolicyVersion || job.CloudApprovalSha256 != RuntimeJson.TextHash(SoftwareCloudPayload.Build(binding, job)))) throw new WorkspaceConflictException("Nội dung chưa được duyệt hoặc policy đã thay đổi.");
    }
    public async Task<object> PreviewSoftwareCloudAsync(string company, string runId, string jobId, CancellationToken ct)
    {
        var events = await ReadEventsAsync(company, 0, ct); var run = RebuildSoftwareRun(company, runId, events.Where(e => e.RunId == runId).ToArray()) ?? throw new KeyNotFoundException();
        var job = run.Jobs.Single(j => j.JobId == jobId && j.Round == run.Round && j.Status == "waiting_cloud_approval"); var binding = Binding(run, job.RoleId, job.AgentId);
        ValidateCloudCurrent(binding, job, events, false); var payload = SoftwareCloudPayload.Build(binding, job);
        return new { run.LastSequence, job.JobId, binding.Cloud, binding.Agent.Name, payload, payloadSha256 = RuntimeJson.TextHash(payload), binding.Configuration.MaxOutputTokens };
    }
    public async Task<SoftwareRun> ApproveSoftwareCloudAsync(string company, string runId, string jobId, CloudJobDecision request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(company, 0, ct); var run = RebuildSoftwareRun(company, runId, events.Where(e => e.RunId == runId).ToArray()) ?? throw new KeyNotFoundException();
            if (run.LastSequence != request.ExpectedSequence || run.State is "paused" or "cancelled") throw new WorkspaceConflictException("Công việc đã thay đổi; xem lại trước khi duyệt.");
            var job = run.Jobs.SingleOrDefault(j => j.JobId == jobId && j.Round == run.Round && j.Status == "waiting_cloud_approval") ?? throw new WorkspaceConflictException("Node không còn chờ duyệt."); var binding = Binding(run, job.RoleId, job.AgentId);
            ValidateCloudCurrent(binding, job, events, false);
            if (request.PayloadSha256 != RuntimeJson.TextHash(SoftwareCloudPayload.Build(binding, job))) throw new WorkspaceConflictException("Nội dung không khớp bản xem trước.");
            await AppendUnsafeAsync(company, runId, [DomainEvent.Create("software.job.changed", job with { Status = "queued", CloudApprovalSha256 = request.PayloadSha256, CloudPolicyVersion = ProfileFrom(events).CloudPolicyVersion }), NodeEvent(job.NodeId, "queued"), DomainEvent.Create("software.cloud.approved", new { job.JobId, request.PayloadSha256, provider = binding.Cloud, policyVersion = ProfileFrom(events).CloudPolicyVersion })], events, ct);
            return RebuildSoftwareRun(company, runId, events.Where(e => e.RunId == runId).ToArray())!;
        }
        finally { _gate.Release(); }
    }
    public async Task VerifySoftwareCloudAsync(RoleBinding binding, SoftwareJob claim, CancellationToken ct)
    {
        var events = await ReadEventsAsync(claim.CompanyId, 0, ct); var run = RebuildSoftwareRun(claim.CompanyId, claim.RunId, events.Where(e => e.RunId == claim.RunId).ToArray()) ?? throw new KeyNotFoundException();
        var job = run.Jobs.Single(j => j.JobId == claim.JobId);
        if (run.State is "paused" or "cancelled" || job.Status != "running" || job.LeaseToken != claim.LeaseToken || job.InputSha256 != claim.InputSha256 || job.LeaseUntil <= DateTimeOffset.UtcNow) throw new WorkspaceConflictException("Lượt chạy không còn hiệu lực.");
        ValidateCloudCurrent(binding, job, events, true);
    }
}

public sealed class RoutedSoftwareClient(OllamaSoftwareClient local, FileEventStore store, RuntimeCredentialVault vault, ICloudProviderClient cloud, ClaudeCodeClient cli) : ISoftwareModelClient
{
    public Task<LocalModel[]> ListAsync(CancellationToken ct) => local.ListAsync(ct);
    public async Task<SoftwareModelResult> GenerateAsync(RoleBinding binding, SoftwareJob job, CancellationToken ct)
    {
        if (binding.Cloud is null) return await local.GenerateAsync(binding, job, ct);
        await store.VerifySoftwareCloudAsync(binding, job, ct);
        var p = binding.Cloud.Provider; var key = vault.Resolve(job.CompanyId, p.ProviderId, p.ProviderType, p.CredentialRef); var payload = SoftwareCloudPayload.Build(binding, job);
        var result = binding.Cloud.Engine == "claude-code" ? await cli.ExecuteAsync(binding.Cloud, payload, key, ct) : await cloud.ExecuteAsync(p, payload, key, ct);
        using var output = JsonDocument.Parse(result.Text);
        return new(output.RootElement.Clone(), result.Usage, RuntimeJson.Hash(output.RootElement));
    }
}

public sealed class ClaudeCodeClient(string dataRoot)
{
    public static AgentCloudRoute Route(CloudProviderSnapshot provider)
    {
        if (provider.ProviderType != "anthropic") throw new ArgumentException("Claude Code dùng Anthropic API key.");
        var path = LocalSetupService.FindCommand("claude") ?? throw new WorkspaceConflictException("Chưa cài Claude Code. Chạy setup với --with-clis hoặc chọn Claude API để dùng ngay.");
        string? entry = null;
        if (Path.GetExtension(path) is ".cmd" or ".bat")
        {
            entry = Path.Combine(Path.GetDirectoryName(path)!, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
            if (!File.Exists(entry)) throw new WorkspaceConflictException("Dùng Claude Code native hoặc bản npm chính thức có cli.js.");
            path = LocalSetupService.FindCommand("node") ?? throw new WorkspaceConflictException("Thiếu Node để chạy Claude Code.");
        }
        string Hash(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
        return new(provider, "claude-code", Path.GetFullPath(path), Hash(path), entry, entry is null ? null : Hash(entry));
    }
    public async Task<CloudProviderResult> ExecuteAsync(AgentCloudRoute route, string payload, string secret, CancellationToken ct)
    {
        string Hash(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
        if (route.Executable is null || !File.Exists(route.Executable) || Hash(route.Executable) != route.ExecutableSha256 || route.EntryPoint is not null && Hash(route.EntryPoint) != route.EntryPointSha256) throw new CloudProviderException("CLAUDE_CODE_CHANGED_RECONNECT_AGENT");
        using var json = JsonDocument.Parse(payload); var root = json.RootElement;
        var directory = LocalFiles.Child(Path.GetFullPath(dataRoot), "cli-sessions", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(120));
        var start = new ProcessStartInfo(route.Executable) { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "COMSPEC", "TEMP", "TMP", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64" }) if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["HOME"] = directory; start.Environment["USERPROFILE"] = directory; start.Environment["CLAUDE_CONFIG_DIR"] = directory;
        start.Environment["ANTHROPIC_API_KEY"] = secret; start.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        start.Environment["CLAUDE_CODE_MAX_OUTPUT_TOKENS"] = root.GetProperty("maxOutputTokens").ToString();
        if (route.EntryPoint is not null) start.ArgumentList.Add(route.EntryPoint);
        foreach (var arg in new[] { "--bare", "--restricted", "-p", "--output-format", "json", "--tools", "", "--disallowedTools", "mcp__*", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--no-session-persistence", "--max-turns", "2", "--max-budget-usd", "0.50", "--model", route.Provider.ModelId, "--json-schema", root.GetProperty("schema").GetRawText(), "--system-prompt", root.GetProperty("instruction").GetString()! }) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start }; var started = false;
        try
        {
            started = process.Start();
            async Task<string> Read(StreamReader reader)
            {
                var result = new StringBuilder(); var buffer = new char[4096]; int count;
                while ((count = await reader.ReadAsync(buffer, deadline.Token)) > 0) { if (result.Length + count > 512 * 1024) { deadline.Cancel(); throw new CloudProviderException("CLAUDE_CODE_RESPONSE_TOO_LARGE"); } result.Append(buffer, 0, count); }
                return result.ToString();
            }
            var stdout = Read(process.StandardOutput); var stderr = Read(process.StandardError);
            await process.StandardInput.WriteAsync(root.GetProperty("context").GetString().AsMemory(), deadline.Token); process.StandardInput.Close();
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token));
            if (process.ExitCode != 0) throw new CloudProviderException("CLAUDE_CODE_FAILED_CHECK_INSTALL_KEY_AND_QUOTA");
            using var response = JsonDocument.Parse(await stdout); var answer = response.RootElement;
            if (answer.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True || !answer.TryGetProperty("structured_output", out var output)) throw new CloudProviderException("CLAUDE_CODE_STRUCTURED_OUTPUT_MISSING");
            var text = output.GetRawText(); if (SourceSafety.ContainsKnownSecret(text)) throw new CloudProviderException("CLOUD_OUTPUT_REJECTED");
            var usage = answer.GetProperty("usage"); int? Number(string name) => usage.TryGetProperty(name, out var n) && n.TryGetInt32(out var value) ? value : null;
            var measured = new ModelUsage(Number("input_tokens"), Number("output_tokens"));
            if (measured.OutputTokens > root.GetProperty("maxOutputTokens").GetInt32()) throw new CloudProviderException("CLAUDE_CODE_OUTPUT_BUDGET_EXCEEDED");
            return new(text, measured, RuntimeJson.TextHash(text), RuntimeJson.Hash(new { route.Provider.ProviderId, usage = measured }));
        }
        finally
        {
            if (started) { try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } } catch (InvalidOperationException) { } }
            Directory.Delete(directory, true);
        }
    }
}
