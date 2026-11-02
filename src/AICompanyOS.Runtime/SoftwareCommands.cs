using System.Text.Json;

namespace AICompanyOS.Runtime;

public sealed partial class FileEventStore
{
    private static SoftwareCommandResult? SoftwareReplay(SoftwareRun run, IEnumerable<DomainEvent> events, string key, string hash)
    {
        var prior = events.FirstOrDefault(e => e.Type == "software.command" && ReadString(e.Data, "key") == key);
        return prior is null ? null : new(ReadString(prior.Data, "hash") == hash ? CommandOutcome.AlreadyApplied : CommandOutcome.Conflict, run, "Command key belongs to another software action.");
    }
    public async Task<SoftwareRun> PreviewExecutionGrantAsync(string company, string runId, long expectedSequence, string imageId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(company, 0, ct); var run = RebuildSoftwareRun(company, runId, events.Where(e => e.RunId == runId).ToArray()) ?? throw new KeyNotFoundException();
            if (run.LastSequence != expectedSequence || run.State != "awaiting_plan_approval" || run.Plan is null) throw new WorkspaceConflictException("Reload a plan awaiting Owner approval.");
            var manifest = RuntimeJson.Hash(new { runId, project = run.Seed.Project, snapshot = run.Seed.Repository.Sha256, plan = run.Plan, bindings = run.Seed.Bindings, imageId,
                permissions = new[] { "repo.read.scoped", "worktree.write.scoped", "container.build.network_none", "container.test.network_none" }, workflow = run.Seed.Workflow });
            var grant = new ExecutionGrant(manifest, imageId, run.Seed.Repository.Sha256, RuntimeJson.Hash(run.Plan), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10));
            await AppendUnsafeAsync(company, runId, [DomainEvent.Create("software.grant.previewed", grant)], events, ct);
            return RebuildSoftwareRun(company, runId, events.Where(e => e.RunId == runId).ToArray())!;
        }
        finally { _gate.Release(); }
    }
    public async Task<SoftwareCommandResult> ApproveSoftwarePlanAsync(string company, string runId, SoftwareApproval request, string key, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(company, 0, ct); var local = events.Where(e => e.RunId == runId).ToArray(); var run = RebuildSoftwareRun(company, runId, local);
            if (run is null) return new(CommandOutcome.NotFound, null);
            var hash = RuntimeJson.Hash(new { action = "approve-plan", request }); var prior = SoftwareReplay(run, local, key, hash); if (prior is not null) return prior;
            if (run.LastSequence != request.ExpectedSequence || run.State != "awaiting_plan_approval" || request.Approved &&
                (run.Grant is null || run.Grant.ManifestSha256 != request.ManifestSha256 || run.Grant.ExpiresAt < DateTimeOffset.UtcNow))
                return new(CommandOutcome.Conflict, run, "Preview the current execution manifest and approve its exact hash before it expires.");
            await AppendUnsafeAsync(company, runId, [NodeEvent("approval-plan", request.Approved ? "approved" : "blocked"),
                DomainEvent.Create("software.command", new { key, hash }), DomainEvent.Create("software.approval.recorded", new { gate = "approval-plan", request.Approved, request.ManifestSha256 })], events, ct);
            await AdvanceSoftwareUnsafeAsync(company, runId, events, ct); return new(CommandOutcome.Applied, RebuildSoftwareRun(company, runId, events.Where(e => e.RunId == runId).ToArray()));
        }
        finally { _gate.Release(); }
    }
    public async Task<SoftwareCommandResult> ControlSoftwareAsync(string company, string runId, string action, long sequence, string key, CancellationToken ct)
    {
        if (action is not ("pause" or "resume" or "step" or "cancel" or "retry")) throw new ArgumentException("Unknown software control.");
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(company, 0, ct); var local = events.Where(e => e.RunId == runId).ToArray(); var run = RebuildSoftwareRun(company, runId, local);
            if (run is null) return new(CommandOutcome.NotFound, null);
            var hash = RuntimeJson.Hash(new { action, sequence }); var replay = SoftwareReplay(run, local, key, hash); if (replay is not null) return replay;
            if (run.LastSequence != sequence || run.State is "completed" or "cancelled" || action is "resume" or "step" && run.State != "paused" || action == "pause" && run.State == "paused" || action == "retry" && run.State != "blocked")
                return new(CommandOutcome.Conflict, run, "Reload a compatible current checkpoint.");
            var additions = new List<DomainEvent> { DomainEvent.Create("software.command", new { key, hash }) };
            if (action == "pause") additions.Add(DomainEvent.Create("software.paused", new { resumeState = run.State }));
            if (action == "step") additions.Add(DomainEvent.Create("software.stepped", new { }));
            if (action == "resume") additions.Add(DomainEvent.Create("software.resumed", new { }));
            if (action == "cancel") additions.Add(DomainEvent.Create("software.cancelled", new { }));
            foreach (var job in run.Jobs.Where(j => j.Round == run.Round))
            {
                if (action is "pause" or "cancel" && job.Status is "running" or "queued" or "waiting_input")
                {
                    if (action == "pause" && job.Status != "running") continue;
                    var status = action == "cancel" ? "cancelled" : "interrupted";
                    additions.Add(DomainEvent.Create("software.job.changed", job with { Status = status, LeaseToken = null, LeaseUntil = null, Error = "OWNER_" + action.ToUpperInvariant() }));
                    additions.Add(NodeEvent(job.NodeId, status));
                }
                if ((action is "resume" or "step" && job.Status == "interrupted" && job.Error == "OWNER_PAUSE" || action == "retry" && job.Status is "failed" or "interrupted") && job.Attempts < run.Seed.Workflow.MaxAttempts)
                {
                    var retryStatus = (job.TaskId is not null || job.NodeId is "ba" or "leader") && Binding(run, job.RoleId, job.AgentId).Model is null ? "waiting_input" : "queued";
                    additions.Add(DomainEvent.Create("software.job.changed", job with { Status = retryStatus, LeaseToken = null, LeaseUntil = null, Error = null }));
                    additions.Add(NodeEvent(job.NodeId, retryStatus)); additions.Add(NodeEvent("subpm", "pending"));
                }
            }
            if (action == "step" && !run.Jobs.Any(j => j.Round == run.Round && (j.Status is "queued" or "waiting_input" || j.Status == "interrupted" && j.Error == "OWNER_PAUSE" && j.Attempts < run.Seed.Workflow.MaxAttempts))) return new(CommandOutcome.Conflict, run, "There is no executable node at this paused checkpoint; resume to inspect the Owner gate.");
            if (action == "retry" && additions.Count == 1) return new(CommandOutcome.Conflict, run, "No failed job has remaining attempts; inspect the report or create a revised run.");
            if (action == "retry" && run.Jobs.Any(j => j.Round == run.Round && j.NodeId == "build" && j.Status is "failed" or "interrupted"))
            {
                foreach (var downstream in run.Jobs.Where(j => j.Round == run.Round && j.NodeId is "tester" or "soc" or "qa"))
                    additions.Add(DomainEvent.Create("software.job.changed", downstream with { Status = "superseded", LeaseToken = null, LeaseUntil = null }));
                foreach (var node in new[] { "tester", "soc", "qa", "gate", "subpm", "final-approval" }) additions.Add(NodeEvent(node, "pending"));
            }
            await AppendUnsafeAsync(company, runId, additions, events, ct); await AdvanceSoftwareUnsafeAsync(company, runId, events, ct);
            return new(CommandOutcome.Applied, RebuildSoftwareRun(company, runId, events.Where(e => e.RunId == runId).ToArray()));
        }
        finally { _gate.Release(); }
    }
    public async Task<SoftwareCommandResult> RequestSoftwareRevisionAsync(string company, string runId, SoftwareRevisionRequest request, string key, CancellationToken ct)
    {
        if (request.Stage is not ("plan" or "delivery") || string.IsNullOrWhiteSpace(request.Feedback) || request.Feedback.Length > 2000 || SourceSafety.ContainsKnownSecret(request.Feedback)) throw new ArgumentException("Provide bounded public feedback for the plan or delivery.");
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(company, 0, ct); var local = events.Where(e => e.RunId == runId).ToArray(); var run = RebuildSoftwareRun(company, runId, local);
            if (run is null) return new(CommandOutcome.NotFound, null);
            var hash = RuntimeJson.Hash(request); var replay = SoftwareReplay(run, local, key, hash); if (replay is not null) return replay;
            if (run.LastSequence != request.ExpectedSequence || (run.OwnerFeedback?.Length ?? 0) >= 6 || request.Stage == "plan" && run.State != "awaiting_plan_approval" || request.Stage == "delivery" && run.State != "awaiting_final_approval")
                return new(CommandOutcome.Conflict, run, "Reload the matching Owner gate. At most six Owner revisions are accepted per run.");
            var additions = new List<DomainEvent> { DomainEvent.Create("software.command", new { key, hash }), DomainEvent.Create("software.owner.feedback", new OwnerFeedback(request.Stage, request.Feedback.Trim(), run.Round, DateTimeOffset.UtcNow)) };
            if (request.Stage == "plan")
            {
                foreach (var job in run.Jobs.Where(j => j.NodeId == "leader")) additions.Add(DomainEvent.Create("software.job.changed", job with { Status = "superseded" }));
                additions.Add(NodeEvent("leader", "pending")); additions.Add(NodeEvent("approval-plan", "pending"));
            }
            else additions.Add(DomainEvent.Create("software.round.started", new { round = run.Round + 1 }));
            await AppendUnsafeAsync(company, runId, additions, events, ct); await AdvanceSoftwareUnsafeAsync(company, runId, events, ct);
            return new(CommandOutcome.Applied, RebuildSoftwareRun(company, runId, events.Where(e => e.RunId == runId).ToArray()));
        }
        finally { _gate.Release(); }
    }
    public async Task<SoftwareCommandResult> ReviewSoftwareDeliveryAsync(string company, string runId, SoftwareReleaseRequest request, string key, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(company, 0, ct); var local = events.Where(e => e.RunId == runId).ToArray(); var run = RebuildSoftwareRun(company, runId, local);
            if (run is null) return new(CommandOutcome.NotFound, null);
            if (request.GitHubToken is not null) throw new ArgumentException("Credentials are accepted only by the explicit release operation, never by review or model inputs.");
            var hash = RuntimeJson.Hash(new { action = "final-review", request }); var replay = SoftwareReplay(run, local, key, hash); if (replay is not null) return replay;
            if (run.LastSequence != request.ExpectedSequence || run.State != "awaiting_final_approval" || run.Candidate?.Sha256 != request.CandidateSha256 ||
                !SoftwareChecks.GatesPass(run) || !request.AcceptanceConfirmed || !request.RightsConfirmed)
                return new(CommandOutcome.Conflict, run, "Review the exact passing candidate and attest acceptance and provenance/rights.");
            await AppendUnsafeAsync(company, runId, [NodeEvent("final-approval", "approved"), DomainEvent.Create("software.final.accepted", new { request.CandidateSha256, request.AcceptanceConfirmed, request.RightsConfirmed }), DomainEvent.Create("software.command", new { key, hash })], events, ct);
            await AdvanceSoftwareUnsafeAsync(company, runId, events, ct);
            return new(CommandOutcome.Applied, RebuildSoftwareRun(company, runId, events.Where(e => e.RunId == runId).ToArray()));
        }
        finally { _gate.Release(); }
    }
    public async Task<SoftwareJob> ClaimManualSoftwareOutputAsync(string company, string runId, string nodeId, long sequence, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(company, 0, ct); var run = RebuildSoftwareRun(company, runId, events.Where(e => e.RunId == runId).ToArray()) ?? throw new KeyNotFoundException();
            var job = run.Jobs.SingleOrDefault(j => j.NodeId == nodeId && j.Round == run.Round && j.Status == "waiting_input");
            if (run.LastSequence != sequence || run.State is "paused" or "cancelled" || job is null || job.Attempts >= run.Seed.Workflow.MaxAttempts || DateTimeOffset.UtcNow - run.Seed.CreatedAt > TimeSpan.FromMinutes(run.Seed.Workflow.MaxElapsedMinutes) || run.StepMode && run.Jobs.Any(j => j.Status == "running")) throw new WorkspaceConflictException("Reload a node waiting for a typed Owner-supplied output.");
            var claim = job with { Status = "running", Attempts = job.Attempts + 1, LeaseToken = Guid.NewGuid().ToString("N"), WorkerId = "owner-supplied", LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(2) };
            await AppendUnsafeAsync(company, runId, [AcknowledgeSoftwareContext(run, claim), DomainEvent.Create("software.job.changed", claim), NodeEvent(nodeId, "running")], events, ct); return claim;
        }
        finally { _gate.Release(); }
    }
    public async Task<SoftwareJob[]> RecoverSoftwareJobsAsync(string workerId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var orphans = new List<SoftwareJob>();
            foreach (var company in CompaniesFrom(await ReadEventsUnsafeAsync(CatalogCompany, 0, ct)))
            {
                var events = await ReadEventsUnsafeAsync(company.CompanyId, 0, ct);
                foreach (var run in SoftwareRunsFrom(company.CompanyId, events))
                    foreach (var job in run.Jobs.Where(j => j.Status == "running" && j.WorkerId != workerId || j.Status == "interrupted" && j.Error == "RESTART_REQUIRES_RECONCILIATION"))
                    {
                        orphans.Add(job);
                        if (job.Status == "running") await AppendUnsafeAsync(company.CompanyId, run.Seed.RunId,
                            [DomainEvent.Create("software.job.changed", job with { Status = "interrupted", LeaseToken = null, LeaseUntil = null, Error = "RESTART_REQUIRES_RECONCILIATION" }), NodeEvent(job.NodeId, "blocked", "RESTART_REQUIRES_RECONCILIATION")], events, ct);
                    }
            }
            return orphans.ToArray();
        }
        finally { _gate.Release(); }
    }
    public async Task<SoftwareRun> RecordSoftwareReleaseAsync(string company, string runId, string candidateSha, ReleaseOutcome release, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(company, 0, ct); var run = RebuildSoftwareRun(company, runId, events.Where(e => e.RunId == runId).ToArray()) ?? throw new KeyNotFoundException();
            if (run.State != "awaiting_release" || run.Candidate?.Sha256 != candidateSha || !SoftwareChecks.GatesPass(run)) throw new WorkspaceConflictException("Release state changed; reconcile the recorded scoped branch before retrying.");
            await AppendUnsafeAsync(company, runId, [DomainEvent.Create("software.release.completed", release), NodeEvent("release", "passed")], events, ct);
            return RebuildSoftwareRun(company, runId, events.Where(e => e.RunId == runId).ToArray())!;
        }
        finally { _gate.Release(); }
    }
}
