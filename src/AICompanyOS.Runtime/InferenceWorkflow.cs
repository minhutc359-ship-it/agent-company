using System.Text.Json;

namespace AICompanyOS.Runtime;

public sealed record SourceExcerpt(string SourceId, int Revision, string Title, string Text, Citation Citation);
public sealed record PriorRoleOutput(string JobId, string RoleId, string OutputSha256, RoleBrief Output);
public sealed record RoleContext(string Schema, string CompanyId, string RunId, string Objective, string AgentId,
    string RoleId, string RoleVersion, string[] SkillVersions, string[] Permissions,
    MemoryRecord[] CanonicalRecords, SourceExcerpt[] Sources, PriorRoleOutput[] PriorOutputs, string[] BudgetWarnings,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DeliveryWork? Delivery = null);
public sealed record InferenceJob(string JobId, string CompanyId, string RunId, string AgentId, string RoleId,
    LocalModel Model, RoleContext Context, string InputSha256, string Status = "queued", int Attempts = 0,
    string? LeaseToken = null, string? WorkerId = null, DateTimeOffset? LeaseUntil = null,
    RoleBrief? Output = null, ModelUsage? Usage = null, string? OutputSha256 = null, string? ErrorCode = null);

public sealed partial class FileEventStore
{
    public const int MaximumModelAttempts = 2;
    private static string LocalRequestHash(string companyId, string objective, string[] sourceRefs, string modelName, string baAgentId, string leaderAgentId) =>
        RuntimeJson.Hash(new { companyId, objective, sourceRefs, modelName, baAgentId, leaderAgentId, mode = "local_model" });

    public async Task<CreateRunResult?> ReplayLocalCreateAsync(string companyId, string objective, string[] sourceRefs, string modelName,
        string key, string baAgentId, string leaderAgentId, CancellationToken ct)
    {
        var events = await ReadEventsAsync(companyId, 0, ct);
        var prior = events.FirstOrDefault(e => e.Type == "workflow.created" && ReadString(e.Data, "idempotencyKey") == key);
        if (prior is null) return null;
        var run = RebuildRun(companyId, prior.RunId, events.Where(e => e.RunId == prior.RunId).ToArray())!;
        return new(run, false, ReadString(prior.Data, "requestHash") != LocalRequestHash(companyId, objective, sourceRefs, modelName, baAgentId, leaderAgentId));
    }

    public async Task<CreateRunResult> CreateLocalRunAsync(string companyId, string runId, string objective, string[] sourceRefs,
        LocalModel model, string key, CancellationToken ct, string baAgentId = "ba-agent", string leaderAgentId = "leader-agent")
    {
        if (!OllamaProvider.ValidModelName(model.Name) || model.Digest.Length != 71 || !model.Digest.StartsWith("sha256:") || !model.Digest[7..].All(char.IsAsciiHexDigit) || sourceRefs.Length > 8)
            throw new ArgumentException("Choose a verified installed model and at most eight source IDs.");
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(companyId, 0, ct);
            var hash = LocalRequestHash(companyId, objective, sourceRefs, model.Name, baAgentId, leaderAgentId);
            var prior = events.FirstOrDefault(e => e.Type == "workflow.created" && ReadString(e.Data, "idempotencyKey") == key);
            if (prior is not null) return new(RebuildRun(companyId, prior.RunId, events.Where(e => e.RunId == prior.RunId).ToArray())!, false, ReadString(prior.Data, "requestHash") != hash);
            var catalog = await ReadEventsUnsafeAsync(CatalogCompany, 0, ct);
            var agents = AgentsFrom(companyId, catalog, events);
            var ba = agents.FirstOrDefault(a => a.AgentId == baAgentId && a.RoleIds.Contains("ba"));
            var leader = agents.FirstOrDefault(a => a.AgentId == leaderAgentId && a.RoleIds.Contains("leader"));
            if (ba is null || leader is null) throw new WorkspaceConflictException("Selected agents must hold BA and Leader roles in this company.");
            var memory = CanonicalFrom(events);
            var sources = SourcesFrom(events);
            foreach (var record in memory)
            {
                ValidateCitations(record.Citations, sources);
                if (record.Citations.Any(c => sources.Where(s => s.SourceId == c.SourceId).Max(s => s.Revision) != c.Revision))
                    throw new WorkspaceConflictException("Canonical memory cites an older source. Review it before starting an AI run.");
            }
            var excerpts = sourceRefs.Select(id =>
            {
                var source = sources.LastOrDefault(s => s.SourceId == id) ?? throw new ArgumentException("AI source IDs must resolve inside this company.");
                var end = Math.Min(source.Content.Length, 2000);
                if (end < source.Content.Length && char.IsLowSurrogate(source.Content[end])) end--;
                return new SourceExcerpt(id, source.Revision, source.Title, source.Content[..end], new(id, source.Revision, 0, end, source.ContentSha256));
            }).ToArray();
            var job = MakeJob(companyId, runId, objective, ba, "ba", model, memory, excerpts, []);
            await AppendUnsafeAsync(companyId, runId,
            [DomainEvent.Create("workflow.created", new { objective, status = "queued", mode = "local_model", model, idempotencyKey = key, requestHash = hash, baAgentId, leaderAgentId, bindings = new[] { ba, leader }, memorySnapshot = memory, sourceRefs }),
             DomainEvent.Create("invocation.queued", job)], events, ct);
            return new(RebuildRun(companyId, runId, events.Where(e => e.RunId == runId).ToArray())!, true);
        }
        finally { _gate.Release(); }
    }

    private static InferenceJob MakeJob(string company, string run, string objective, Agent agent, string roleId, LocalModel model,
        MemoryRecord[] memory, SourceExcerpt[] sources, PriorRoleOutput[] outputs, string[]? inheritedWarnings = null, DeliveryWork? delivery = null)
    {
        var role = BuiltInCatalog.Roles.Single(r => r.RoleId == roleId);
        var context = new RoleContext("ai-company-os/role-context/v1", company, run, objective, agent.AgentId, roleId, role.Version,
            role.SkillIds.Select(s => s + "@1.0.0").ToArray(), [], memory, sources, outputs, inheritedWarnings ?? [], delivery);
        if (SourceSafety.ContainsKnownSecret(OllamaProvider.SerializeContext(context)))
            throw new ArgumentException("Possible credentials in the selected context must be removed before model invocation.");
        // Only untrusted source excerpts may shrink. Policy, canonical versions, objective and prior role output stay intact.
        while (System.Text.Encoding.UTF8.GetByteCount(OllamaProvider.SerializeContext(context)) > 10000)
        {
            var largest = context.Sources.OrderByDescending(s => JsonSerializer.SerializeToUtf8Bytes(s, JsonOptions).Length).FirstOrDefault(s => s.Text.Length > 64);
            if (largest is null) throw new WorkspaceConflictException("Pinned context exceeds the 10,000-byte budget. Reduce the task or reviewed memory; mandatory records were not dropped.");
            var length = Math.Max(64, largest.Text.Length / 2);
            if (length < largest.Text.Length && char.IsLowSurrogate(largest.Text[length])) length--;
            var shortened = largest with { Text = largest.Text[..length], Citation = largest.Citation with { End = largest.Citation.Start + length } };
            context = context with { Sources = context.Sources.Select(s => ReferenceEquals(s, largest) ? shortened : s).ToArray(),
                BudgetWarnings = ["Source excerpts were shortened to fit the input budget. Exact immutable source revisions and ranges are included."] };
        }
        var job = new InferenceJob("job-" + Guid.NewGuid().ToString("N"), company, run, agent.AgentId, roleId, model, context, "");
        return job with { InputSha256 = RuntimeJson.Hash(OllamaProvider.Payload(job)) };
    }

    private static InferenceJob[] JobsFrom(IEnumerable<DomainEvent> events)
    {
        var jobs = new Dictionary<string, InferenceJob>();
        foreach (var e in events)
        {
            if (e.Type == "invocation.queued")
            {
                var job = e.Data.Deserialize<InferenceJob>(JsonOptions)!;
                jobs[job.JobId] = job;
            }
            else if (e.Type is "invocation.started" or "invocation.heartbeat" or "invocation.completed" or "invocation.failed" or "invocation.interrupted" or "invocation.retry_requested")
            {
                var job = e.Data.Deserialize<InferenceJob>(JsonOptions)!;
                jobs[job.JobId] = job;
            }
        }
        return jobs.Values.ToArray();
    }

    public async Task RecoverModelJobsAsync(string workerId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            foreach (var company in CompaniesFrom(await ReadEventsUnsafeAsync(CatalogCompany, 0, ct)))
            {
                var events = await ReadEventsUnsafeAsync(company.CompanyId, 0, ct);
                foreach (var job in JobsFrom(events).Where(j => j.Status == "running" && j.WorkerId != workerId))
                    await AppendUnsafeAsync(company.CompanyId, job.RunId,
                        [DomainEvent.Create("invocation.interrupted", job with { Status = "interrupted", LeaseToken = null, ErrorCode = "RESTART_REQUIRES_RETRY" })], events, ct);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<InferenceJob?> ClaimModelJobAsync(string workerId, DateTimeOffset now, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            foreach (var company in CompaniesFrom(await ReadEventsUnsafeAsync(CatalogCompany, 0, ct)))
            {
                var events = await ReadEventsUnsafeAsync(company.CompanyId, 0, ct);
                foreach (var expired in JobsFrom(events).Where(j => j.Status == "running" && j.LeaseUntil <= now))
                    await AppendUnsafeAsync(company.CompanyId, expired.RunId,
                        [DomainEvent.Create("invocation.interrupted", expired with { Status = "interrupted", LeaseToken = null, ErrorCode = "LEASE_EXPIRED" })], events, ct);
                foreach (var pending in JobsFrom(events).Where(j => j.Status == "queued" && j.Attempts < MaximumModelAttempts))
                {
                    var run = RebuildRun(company.CompanyId, pending.RunId, events.Where(e => e.RunId == pending.RunId).ToArray());
                    if (run?.State != "queued") continue;
                    var job = pending with { Status = "running", Attempts = pending.Attempts + 1, WorkerId = workerId, LeaseToken = Guid.NewGuid().ToString("N"), LeaseUntil = now.AddSeconds(30), ErrorCode = null };
                    await AppendUnsafeAsync(company.CompanyId, pending.RunId, [DomainEvent.Create("invocation.started", job)], events, ct);
                    return job;
                }
            }
            return null;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> HeartbeatModelJobAsync(InferenceJob claim, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(claim.CompanyId, 0, ct);
            var job = JobsFrom(events).FirstOrDefault(j => j.JobId == claim.JobId);
            var run = RebuildRun(claim.CompanyId, claim.RunId, events.Where(e => e.RunId == claim.RunId).ToArray());
            if (job?.Status != "running" || job.LeaseToken != claim.LeaseToken || job.LeaseUntil <= DateTimeOffset.UtcNow || run?.State != "running") return false;
            await AppendUnsafeAsync(claim.CompanyId, claim.RunId,
                [DomainEvent.Create("invocation.heartbeat", job with { LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(30) })], events, ct);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> FinishModelJobAsync(InferenceJob claim, ModelResult? result, string? errorCode, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(claim.CompanyId, 0, ct);
            var job = JobsFrom(events).FirstOrDefault(j => j.JobId == claim.JobId);
            if (job?.Status != "running" || job.LeaseToken != claim.LeaseToken || job.LeaseUntil <= DateTimeOffset.UtcNow) return false;
            var runEvents = events.Where(e => e.RunId == job.RunId).ToArray();
            var run = RebuildRun(job.CompanyId, job.RunId, runEvents)!;
            if (run.State is "paused" or "cancelled")
            {
                await AppendUnsafeAsync(job.CompanyId, job.RunId, [DomainEvent.Create("invocation.interrupted", job with
                    { Status = "interrupted", LeaseToken = null, ErrorCode = run.State == "paused" ? "OWNER_PAUSED" : "OWNER_CANCELLED" })], events, ct);
                return true;
            }
            if (result is null)
            {
                await AppendUnsafeAsync(job.CompanyId, job.RunId, [DomainEvent.Create("invocation.failed", job with
                    { Status = "failed", LeaseToken = null, ErrorCode = errorCode ?? "MODEL_REQUEST_FAILED" })], events, ct);
                return true;
            }
            var draft = job.RoleId == "dev" ? DeliveryTools.ValidateDraft(result.Draft) : null;
            var output = draft is null ? OllamaProvider.ParseBrief(JsonSerializer.Serialize(result.Output, JsonOptions)) : new RoleBrief(draft.Summary, draft.Files.Select(f => f.Path).ToArray(), []);
            if ((draft is null ? RuntimeJson.Hash(output) : RuntimeJson.Hash(draft)) != result.OutputSha256) throw new InvalidDataException("Model output digest mismatch.");
            var completed = job with { Status = "completed", Output = output, Usage = result.Usage, OutputSha256 = result.OutputSha256, LeaseToken = null };
            var additions = new List<DomainEvent> { DomainEvent.Create("invocation.completed", completed) };
            if (job.RoleId == "ba")
            {
                var created = runEvents.First(e => e.Type == "workflow.created");
                var refs = created.Data.GetProperty("sourceRefs").Deserialize<string[]>(JsonOptions)!;
                var spec = new SpecArtifact("spec-" + Guid.NewGuid().ToString("N"), run.Objective,
                    refs.Append($"output/{job.JobId}@{result.OutputSha256}").ToArray(), "produced_by_model", job.Context.CanonicalRecords);
                additions.Add(DomainEvent.Create("spec.produced", spec));
                additions.Add(DomainEvent.Create("handoff.requested", HandoffPacket.Create(job.CompanyId, job.RunId, spec, run.LeaderAgentId!, job.AgentId)));
            }
            else if (job.RoleId == "leader")
            {
                additions.Add(DomainEvent.Create("plan.produced", new DeliveryPlan("plan-" + Guid.NewGuid().ToString("N"), run.Handoff!.HandoffId, output.Deliverables, "produced_by_model")));
                additions.Add(DomainEvent.Create("approval.required", new { approvalId = "approval-" + Guid.NewGuid().ToString("N"), action = "approve-plan" }));
            }
            else if (job.RoleId == "dev" && run.Delivery is not null && draft is not null)
                additions.AddRange(CheckArtifactEvents(run.Delivery, draft, "local_model", job.JobId));
            else throw new InvalidDataException("No typed output handler exists for the invoked role.");
            await AppendUnsafeAsync(job.CompanyId, job.RunId, additions, events, ct);
            return true;
        }
        finally { _gate.Release(); }
    }

    private static InferenceJob LeaderJob(WorkflowRun run, IReadOnlyList<DomainEvent> events)
    {
        var created = events.First(e => e.Type == "workflow.created");
        var leader = created.Data.GetProperty("bindings").Deserialize<Agent[]>(JsonOptions)!.First(a => a.AgentId == run.LeaderAgentId && a.RoleIds.Contains("leader"));
        var ba = JobsFrom(events).Single(j => j.RoleId == "ba" && j.Status == "completed");
        return MakeJob(run.CompanyId, run.RunId, run.Objective, leader, "leader", ba.Model, ba.Context.CanonicalRecords, ba.Context.Sources,
            [new(ba.JobId, "ba", ba.OutputSha256!, ba.Output!)], ba.Context.BudgetWarnings);
    }

    public async Task<CommandResult> RetryModelJobAsync(string company, string runId, long expectedSequence, string key, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(company, 0, ct);
            var runEvents = events.Where(e => e.RunId == runId).ToArray();
            var run = RebuildRun(company, runId, runEvents);
            if (run is null) return new(CommandOutcome.NotFound, null);
            var prior = runEvents.FirstOrDefault(e => e.Type == "workflow.retry.recorded" && ReadString(e.Data, "key") == key);
            if (prior is not null) return new(CommandOutcome.AlreadyApplied, run);
            if (run.LastSequence != expectedSequence || run.State != "blocked" || run.ExecutionMode != "local_model")
                return new(CommandOutcome.Conflict, run, "Reload a blocked AI run before retrying.");
            var job = JobsFrom(runEvents).LastOrDefault(j => j.Status is "failed" or "interrupted");
            if (job is null || job.Attempts >= MaximumModelAttempts) return new(CommandOutcome.Conflict, run, "The two-attempt budget is exhausted. Start a new reviewed run.");
            var queued = job with { Status = "queued", LeaseToken = null, LeaseUntil = null, ErrorCode = null };
            await AppendUnsafeAsync(company, runId,
                [DomainEvent.Create("invocation.retry_requested", JsonSerializer.SerializeToElement(queued, JsonOptions)), DomainEvent.Create("workflow.retry.recorded", new { key })], events, ct);
            return new(CommandOutcome.Applied, RebuildRun(company, runId, events.Where(e => e.RunId == runId).ToArray()));
        }
        finally { _gate.Release(); }
    }
}
