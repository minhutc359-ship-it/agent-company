using System.Net;
using System.Text;
using System.Text.Json;
using AICompanyOS.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

public static class InferenceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static int _checks;
    private static LocalModel Model => new("fixture:latest", "sha256:" + new string('a', 64), "test", DateTimeOffset.UtcNow);
    public static async Task RunAsync()
    {
        foreach (var backend in new[] { "file", "sqlite" }) await FlowAsync(backend);
        await RecoveryAsync();
        await ProtocolAsync();
        await BudgetAndCancellationAsync();
        Console.WriteLine($"PASS: {_checks} local-inference assertions: role-context pins, protocol validation, lease fencing, bounded retry, pause/cancel/restart and storage parity. Model responses use fixtures, not installed LLM weights.");
    }
    private static void Check(bool value, string message) { _checks++; if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); } catch (T) { _checks++; return; }
        throw new InvalidOperationException(message);
    }
    private static string Temp() => Path.Combine(Path.GetTempPath(), "aicos-inference-" + Guid.NewGuid().ToString("N"));
    private static async Task<WorkflowRun> Create(FileEventStore store, string id) =>
        (await store.CreateLocalRunAsync("software-studio", id, "Keep all approved constraints", [], Model, id, Ct)).Run;
    private static ModelResult Result(string text) { var brief = new RoleBrief(text, ["Review evidence", "Ask Owner before implementation"], []); return new(brief, new(120, 40), RuntimeJson.Hash(brief)); }
    private static async Task FlowAsync(string backend)
    {
        var root = Temp();
        try
        {
            using var store = new FileEventStore(root, backend); await store.InitializeAsync(Ct);
            var source = await store.ImportSourceAsync("software-studio", new("policy", "Project policy", "Keep data local. Do not auto-push."), Ct);
            var citation = new Citation(source.SourceId, source.Revision, 0, source.Content.Length, source.ContentSha256);
            await store.ProposeMemoryAsync("software-studio", new("memory", "privacy", "constraint", "Keep data local.", [citation]), Ct);
            await store.ReviewMemoryAsync("software-studio", "memory", new(true, 0), Ct);
            await Throws<ArgumentException>(() => store.CreateLocalRunAsync("software-studio", "foreign", "Invalid scope", ["not-in-this-company"], Model, "foreign", Ct), "Foreign/unresolved sources must be rejected before enqueue.");
            var created = await store.CreateLocalRunAsync("software-studio", "run-ai", "Keep all approved constraints", ["policy"], Model, "run-ai", Ct);
            var run = created.Run;
            Check(run.State == "queued" && run.Spec is null && run.ExecutionMode == "local_model", "Real AI runs must queue without scaffold output.");
            var job = run.Invocations!.Single();
            Check(job.Context.CanonicalRecords.Single().Version == 1 && job.Context.Sources.Single().Citation == citation, "Source revisions and accepted constraints must pin into context.");
            Check(job.Context.Permissions.Length == 0 && job.Context.RoleId == "ba", "Model context cannot grant tools.");
            var replay = await store.CreateLocalRunAsync("software-studio", "unused", "Keep all approved constraints", ["policy"], Model with { ObservedAt = DateTimeOffset.UtcNow.AddHours(1) }, "run-ai", Ct);
            Check(!replay.Created && replay.Run.RunId == run.RunId, "Catalog observation timestamps must not break create idempotency.");
            var changed = await store.ReplayLocalCreateAsync("software-studio", "Changed", ["policy"], Model.Name, "run-ai", "ba-agent", "leader-agent", Ct);
            Check(changed!.Conflict, "Reusing a model create key with a changed payload must conflict.");
            var claim = await store.ClaimModelJobAsync("worker-one", DateTimeOffset.UtcNow, Ct);
            Check(claim?.Attempts == 1 && claim.LeaseToken is not null, "Claim must persist a lease and attempt.");
            Check(await store.ClaimModelJobAsync("worker-two", DateTimeOffset.UtcNow, Ct) is null, "Another worker cannot claim an active lease.");
            Check(await store.HeartbeatModelJobAsync(claim!, Ct), "An owned live lease must heartbeat.");
            Check(await store.FinishModelJobAsync(claim!, Result("BA analysis"), null, Ct), "A valid BA result must commit.");
            var eventsAfter = (await store.ReadEventsAsync("software-studio", 0, Ct)).Count;
            Check(!await store.FinishModelJobAsync(claim!, Result("Duplicate output"), null, Ct) && (await store.ReadEventsAsync("software-studio", 0, Ct)).Count == eventsAfter, "Duplicate completion must not append or overwrite output.");
            run = (await store.GetRunAsync("software-studio", run.RunId, Ct))!;
            Check(run.State == "awaiting_handoff" && run.Spec!.VerificationState == "produced_by_model", "BA completion must create a real-output handoff, not verified engineering output.");
            var ack = await store.AcknowledgeHandoffAsync("software-studio", run.RunId, run.Handoff!.ContentSha256, "leader-agent", "ai-ack", Ct, run.LastSequence);
            Check(ack.Run?.State == "queued" && ack.Run.Plan is null, "AI ACK must enqueue Leader instead of producing a scaffold plan.");
            var leaderJob = ack.Run!.Invocations!.Last();
            Check(leaderJob.Context.PriorOutputs.Single().Output.Summary == "BA analysis" && leaderJob.Context.PriorOutputs.Single().OutputSha256 == RuntimeJson.Hash(Result("BA analysis").Output), "Leader must receive the exact BA output and digest.");
            var leader = (await store.ListAgentsAsync("software-studio", Ct)).Single(a => a.AgentId == "leader-agent");
            await store.AssignRolesAsync("software-studio", leader.AgentId, new(["dev"], leader.Version), Ct);
            Check((await store.ClaimModelJobAsync("worker-one", DateTimeOffset.UtcNow, Ct))!.Context.RoleId == "leader", "Existing jobs must keep the pinned role after registry edits.");
            var activeLeader = (await store.GetRunAsync("software-studio", run.RunId, Ct))!.Invocations!.Last();
            await store.FinishModelJobAsync(activeLeader, Result("Leader plan"), null, Ct);
            run = (await store.GetRunAsync("software-studio", run.RunId, Ct))!;
            Check(run.State == "awaiting_approval" && run.Plan!.Steps.SequenceEqual(Result("Leader plan").Output.Deliverables), "Leader result must populate plan steps and require Owner approval.");
            var approved = await store.DecideApprovalAsync("software-studio", run.RunId, true, "ai-owner", Ct, run.LastSequence);
            var report = WorkflowReport.FromRun(approved.Run!);
            Check(report.ModelInvocations!.All(j => j.Status == "completed") && report.EvidenceRefs.Length == 0 && report.Limitations.Any(s => s.Contains("No code tools")), "AI receipts must not masquerade as software test evidence.");
            await store.AssignRolesAsync("software-studio", leader.AgentId, new(["leader"], leader.Version + 1), Ct);
            var paused = await Create(store, "run-pause");
            var pausedBeforeClaim = await store.ControlRunAsync("software-studio", paused.RunId, "pause", paused.LastSequence, "pause-before", Ct);
            Check(await store.ClaimModelJobAsync("worker-one", DateTimeOffset.UtcNow, Ct) is null, "Queued paused runs must not dispatch.");
            await store.ControlRunAsync("software-studio", paused.RunId, "resume", pausedBeforeClaim.Run!.LastSequence, "resume-before", Ct);
            var first = (await store.ClaimModelJobAsync("worker-one", DateTimeOffset.UtcNow, Ct))!;
            paused = (await store.GetRunAsync("software-studio", paused.RunId, Ct))!;
            var interrupted = await store.ControlRunAsync("software-studio", paused.RunId, "pause", paused.LastSequence, "pause-active", Ct);
            Check(!await store.FinishModelJobAsync(first, Result("Late result"), null, Ct), "Pause must immediately fence an old completion.");
            await store.ControlRunAsync("software-studio", paused.RunId, "resume", interrupted.Run!.LastSequence, "resume-active", Ct);
            var second = (await store.ClaimModelJobAsync("worker-one", DateTimeOffset.UtcNow, Ct))!;
            Check(second.Attempts == 2 && second.LeaseToken != first.LeaseToken && second.InputSha256 == first.InputSha256, "Resume must use a new lease with identical pinned input and bounded attempt count.");
            paused = (await store.GetRunAsync("software-studio", paused.RunId, Ct))!;
            interrupted = await store.ControlRunAsync("software-studio", paused.RunId, "pause", paused.LastSequence, "pause-budget", Ct);
            var exhausted = await store.ControlRunAsync("software-studio", paused.RunId, "resume", interrupted.Run!.LastSequence, "resume-budget", Ct);
            Check(exhausted.Run?.State == "blocked" && exhausted.Run.LastError == "ATTEMPT_BUDGET_EXHAUSTED", "Repeated interruption cannot bypass the attempt cap.");
            Check((await store.RetryModelJobAsync("software-studio", paused.RunId, exhausted.Run!.LastSequence, "over-budget", Ct)).Outcome == CommandOutcome.Conflict, "Owner retry must also respect the attempt cap.");
            var cancelled = await Create(store, "run-cancel");
            var cancelledClaim = (await store.ClaimModelJobAsync("worker-one", DateTimeOffset.UtcNow, Ct))!;
            cancelled = (await store.GetRunAsync("software-studio", cancelled.RunId, Ct))!;
            var cancel = await store.ControlRunAsync("software-studio", cancelled.RunId, "cancel", cancelled.LastSequence, "cancel-active", Ct);
            Check(cancel.Run?.State == "cancelled" && !await store.FinishModelJobAsync(cancelledClaim, Result("Late cancelled"), null, Ct), "Cancel must fence all pending outputs.");
            var failing = await Create(store, "run-failure");
            var worker = new InferenceWorker(store, new FailingProvider(), NullLogger<InferenceWorker>.Instance);
            await worker.RunOneAsync(Ct);
            failing = (await store.GetRunAsync("software-studio", failing.RunId, Ct))!;
            Check(failing.State == "blocked" && failing.LastError == "MODEL_OUTPUT_SCHEMA_INVALID", "Provider schema failure must block the workflow with a safe code.");
            var retry = await store.RetryModelJobAsync("software-studio", failing.RunId, failing.LastSequence, "retry-once", Ct);
            var repeatRetry = await store.RetryModelJobAsync("software-studio", failing.RunId, failing.LastSequence, "retry-once", Ct);
            Check(retry.Outcome == CommandOutcome.Applied && repeatRetry.Outcome == CommandOutcome.AlreadyApplied, "Retry keys must be idempotent.");
            await worker.RunOneAsync(Ct);
            failing = (await store.GetRunAsync("software-studio", failing.RunId, Ct))!;
            Check(failing.Invocations!.Single().Attempts == 2 && (await store.RetryModelJobAsync("software-studio", failing.RunId, failing.LastSequence, "retry-third", Ct)).Outcome == CommandOutcome.Conflict, "Failed inference must never run a third attempt automatically.");
            var manifest = await store.VerifyAsync(Ct);
            await store.MigrateAsync(backend == "file" ? "sqlite" : "file", manifest.Digest, Ct);
            Check((await store.VerifyAsync(Ct)).Digest == manifest.Digest && (await store.GetRunAsync("software-studio", "run-ai", Ct))!.Invocations!.Length == 2, "Model context, receipts and output digests must survive storage migration.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static async Task RecoveryAsync()
    {
        var root = Temp(); InferenceJob old;
        try
        {
            using (var store = new FileEventStore(root)) { await store.InitializeAsync(Ct); await Create(store, "run-recovery"); old = (await store.ClaimModelJobAsync("dead-worker", DateTimeOffset.UtcNow, Ct))!; }
            using var restarted = new FileEventStore(root);
            await restarted.RecoverModelJobsAsync("new-worker", Ct);
            var run = (await restarted.GetRunAsync("software-studio", "run-recovery", Ct))!;
            Check(run.State == "blocked" && run.LastError == "RESTART_REQUIRES_RETRY", "Restart must expose an interrupted invocation instead of silently repeating it.");
            Check(await restarted.ClaimModelJobAsync("new-worker", DateTimeOffset.UtcNow, Ct) is null && !await restarted.FinishModelJobAsync(old, Result("Late dead worker"), null, Ct), "An old worker cannot dispatch or complete after recovery.");
            await restarted.RetryModelJobAsync("software-studio", run.RunId, run.LastSequence, "after-restart", Ct);
            var expired = (await restarted.ClaimModelJobAsync("new-worker", DateTimeOffset.UtcNow.AddMinutes(-1), Ct))!;
            Check(!await restarted.FinishModelJobAsync(expired, Result("Expired"), null, Ct), "Expired leases must reject completion.");
            Check(await restarted.ClaimModelJobAsync("new-worker", DateTimeOffset.UtcNow, Ct) is null, "Expired lease recovery must not auto-retry.");
            run = (await restarted.GetRunAsync("software-studio", run.RunId, Ct))!;
            Check(run.State == "blocked" && run.LastError == "LEASE_EXPIRED", "Expired lease state must be visible in the persisted report.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static async Task ProtocolAsync()
    {
        foreach (var url in new[] { "https://127.0.0.1:11434", "http://localhost:11434", "http://127.0.0.1:4173", "http://example.com", "http://user@127.0.0.1:11434" })
            await Throws<ArgumentException>(() => Task.Run(() => { using var client = new OllamaProvider(url); }), "Provider URLs must be exact loopback without credentials, DNS or redirects.");
        var root = Temp();
        try
        {
            using var store = new FileEventStore(root); await store.InitializeAsync(Ct); var run = await Create(store, "run-protocol"); var job = run.Invocations!.Single();
            var handler = new ProtocolHandler(); using var provider = new OllamaProvider("http://127.0.0.1:11434", handler);
            Check((await provider.ListAsync(Ct)).Single().Name == Model.Name, "Cloud/remote models must be filtered from catalog.");
            var result = await provider.GenerateAsync(job, Ct);
            Check(result.Output.Summary == "Structured local result" && result.Usage.OutputTokens == 23, "Ollama response must preserve typed output and actual usage.");
            Check(handler.SentPayloadHash == job.InputSha256 && !handler.SentTools, "Receipt digest must match the exact dispatched payload with no tools.");
            var claim = (await store.ClaimModelJobAsync("protocol-worker", DateTimeOffset.UtcNow, Ct))!;
            await store.FinishModelJobAsync(claim, result, null, Ct);
            Check(!JsonSerializer.Serialize(await store.ReadEventsAsync("software-studio", 0, Ct)).Contains("PRIVATE_THINKING_MARKER"), "Provider thinking metadata must never be persisted.");
            foreach (var mode in new[] { "schema", "tool", "length", "remote", "small-context", "oversize", "usage", "redirect" })
            {
                handler.Mode = mode;
                await Throws<ModelProviderException>(() => provider.GenerateAsync(job, Ct), $"Provider must reject {mode} responses.");
            }
            handler.Mode = "ok";
            var hiddenSecret = "{\"summary\":\"\\u0073\\u006b-proj-123456789012345678901234567890\",\"deliverables\":[\"x\"],\"questions\":[]}";
            await Throws<ModelProviderException>(() => Task.Run(() => OllamaProvider.ParseBrief(hiddenSecret)), "Escaped credentials must be rejected after JSON decoding too.");
            await Throws<ModelProviderException>(() => provider.GenerateAsync(job with { InputSha256 = "changed" }, Ct), "Changed input digest must be rejected before chat.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static async Task BudgetAndCancellationAsync()
    {
        var root = Temp();
        try
        {
            using var store = new FileEventStore(root); await store.InitializeAsync(Ct);
            var text = string.Concat(Enumerable.Repeat("Dữ liệu có nguồn. ", 200));
            for (var i = 0; i < 4; i++) await store.ImportSourceAsync("software-studio", new("large-" + i, "Nguồn " + i, text), Ct);
            var packed = (await store.CreateLocalRunAsync("software-studio", "run-packed", "Bảo toàn ràng buộc", ["large-0", "large-1", "large-2", "large-3"], Model, "packed", Ct)).Run;
            var context = packed.Invocations!.Single().Context;
            Check(context.BudgetWarnings.Length > 0 && Encoding.UTF8.GetByteCount(OllamaProvider.SerializeContext(context)) <= 10000 && context.Sources.Length == 4, "Oversized excerpts must pack under budget with an explicit warning and all selected source IDs.");
            Check(context.Sources.All(s => s.Text == text[s.Citation.Start..s.Citation.End] && s.Citation.ContentSha256 == RuntimeJson.TextHash(text)), "Packed citations must still resolve to exact immutable source ranges.");
            var pending = new PendingProvider();
            var worker = new InferenceWorker(store, pending, NullLogger<InferenceWorker>.Instance);
            var execution = worker.RunOneAsync(Ct);
            await pending.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            packed = (await store.GetRunAsync("software-studio", packed.RunId, Ct))!;
            await store.ControlRunAsync("software-studio", packed.RunId, "pause", packed.LastSequence, "pause-request", Ct);
            await execution.WaitAsync(TimeSpan.FromSeconds(8));
            var paused = (await store.GetRunAsync("software-studio", packed.RunId, Ct))!;
            Check(pending.Cancelled && paused.State == "paused" && paused.Spec is null && paused.Invocations!.Single().Output is null, "Pause must cancel the client request cooperatively and must not adopt its late output.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class PendingProvider : ILocalModelProvider
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public Task<LocalModel[]> ListAsync(CancellationToken ct) => Task.FromResult(new[] { Model });
        public async Task<ModelResult> GenerateAsync(InferenceJob job, CancellationToken ct)
        {
            Started.SetResult(true);
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            throw new InvalidOperationException("Unreachable.");
        }
    }
    private sealed class FailingProvider : ILocalModelProvider
    {
        public Task<LocalModel[]> ListAsync(CancellationToken ct) => Task.FromResult(new[] { Model });
        public Task<ModelResult> GenerateAsync(InferenceJob job, CancellationToken ct) => Task.FromException<ModelResult>(new ModelProviderException("MODEL_OUTPUT_SCHEMA_INVALID"));
    }
    private sealed class ProtocolHandler : HttpMessageHandler
    {
        public string Mode { get; set; } = "ok";
        public string? SentPayloadHash { get; private set; }
        public bool SentTools { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object payload;
            if (request.RequestUri!.AbsolutePath == "/api/tags") payload = new { models = new object[] {
                new { name = Model.Name, digest = new string('a', 64), details = new { format = "gguf", parameter_size = "test" } },
                new { name = "remote:cloud", digest = new string('b', 64), remote_host = "https://external.invalid", details = new { format = "gguf" } } } };
            else if (request.RequestUri.AbsolutePath == "/api/show") payload = new { details = new { format = "gguf" }, capabilities = new[] { "completion" }, model_info = new Dictionary<string,int> { ["test.context_length"] = Mode == "small-context" ? 1024 : 32768 }, remote_host = Mode == "remote" ? "https://external.invalid" : "" };
            else
            {
                var text = await request.Content!.ReadAsStringAsync(ct); using var body = JsonDocument.Parse(text);
                SentPayloadHash = RuntimeJson.Hash(body.RootElement); SentTools = body.RootElement.TryGetProperty("tools", out _);
                if (Mode == "redirect") return new(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("https://external.invalid") } };
                if (Mode == "oversize") return new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 300000)) };
                var output = Mode == "schema" ? "{\"summary\":\"x\",\"deliverables\":[\"x\"],\"questions\":[],\"approved\":true}" : JsonSerializer.Serialize(new RoleBrief("Structured local result", ["Review evidence"], []), RuntimeJson.Options);
                payload = new { model = Model.Name, done = true, done_reason = Mode == "length" ? "length" : "stop", message = new { role = "assistant", content = output, thinking = "PRIVATE_THINKING_MARKER", tool_calls = Mode == "tool" ? new[] { new { name = "shell" } } : [] }, prompt_eval_count = 120, eval_count = Mode == "usage" ? 999999 : 23 };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload, RuntimeJson.Options), Encoding.UTF8, "application/json") };
        }
    }
}
