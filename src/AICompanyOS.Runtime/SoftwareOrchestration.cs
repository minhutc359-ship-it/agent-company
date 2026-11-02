using System.Text;
using System.Text.Json;

namespace AICompanyOS.Runtime;

public sealed record SoftwareCommandResult(CommandOutcome Outcome, SoftwareRun? Run, string? Message = null);

public sealed partial class FileEventStore
{
    private static SoftwareRun? RebuildSoftwareRun(string company, string runId, IReadOnlyList<DomainEvent> events)
    {
        var created = events.FirstOrDefault(e => e.Type == "software.created"); if (created is null) return null;
        var seed = created.Data.Deserialize<SoftwareSeed>(JsonOptions)!;
        var nodes = seed.Workflow.Nodes.ToDictionary(n => n.Id, n => new SoftwareNodeState(n.Id, "pending"));
        var jobs = new Dictionary<string, SoftwareJob>(); var patches = new List<TaskPatch>(); var receipts = new List<ExecutionReceipt>();
        RequirementSpec? spec = null; TaskGraph? plan = null; IntegratedCandidate? candidate = null; ExecutionGrant? grant = null; ReleaseOutcome? release = null;
        var handoffs = new List<SoftwareHandoff>(); var acks = new List<SoftwareHandoffAck>();
        var round = 0; var stepMode = false; var feedback = new List<OwnerFeedback>(); string? control = null, resume = null, error = null;
        foreach (var e in events)
        {
            switch (e.Type)
            {
                case "software.handoff.created": handoffs.Add(e.Data.Deserialize<SoftwareHandoff>(JsonOptions)!); break;
                case "software.handoff.acknowledged": acks.Add(e.Data.Deserialize<SoftwareHandoffAck>(JsonOptions)!); break;
                case "software.node.changed": var node = e.Data.Deserialize<SoftwareNodeState>(JsonOptions)!; nodes[node.Id] = node; break;
                case "software.job.changed": var job = e.Data.Deserialize<SoftwareJob>(JsonOptions)!; jobs[job.JobId] = job; break;
                case "software.job.heartbeat":
                    var id = ReadString(e.Data, "jobId")!; if (jobs.TryGetValue(id, out var active)) jobs[id] = active with { LeaseUntil = e.Data.GetProperty("leaseUntil").GetDateTimeOffset() }; break;
                case "software.spec.produced": spec = e.Data.Deserialize<RequirementSpec>(JsonOptions); break;
                case "software.plan.produced": plan = e.Data.Deserialize<TaskGraph>(JsonOptions); break;
                case "software.patch.applied": patches.Add(e.Data.Deserialize<TaskPatch>(JsonOptions)!); break;
                case "software.candidate.produced": candidate = e.Data.Deserialize<IntegratedCandidate>(JsonOptions); break;
                case "software.receipt.recorded": receipts.Add(e.Data.Deserialize<ExecutionReceipt>(JsonOptions)!); break;
                case "software.grant.previewed": grant = e.Data.Deserialize<ExecutionGrant>(JsonOptions); break;
                case "software.release.completed": release = e.Data.Deserialize<ReleaseOutcome>(JsonOptions); break;
                case "software.error": error = ReadString(e.Data, "code"); break;
                case "software.paused": control = "paused"; stepMode = false; resume = ReadString(e.Data, "resumeState"); break;
                case "software.stepped": control = null; stepMode = true; break;
                case "software.owner.feedback":
                    var ownerFeedback = e.Data.Deserialize<OwnerFeedback>(JsonOptions)!; feedback.Add(ownerFeedback); error = null;
                    if (ownerFeedback.Stage == "plan") { plan = null; grant = null; }
                    break;
                case "software.resumed": control = null; resume = null; stepMode = false; break;
                case "software.cancelled": control = "cancelled"; stepMode = false; break;
                case "software.round.started":
                    round = e.Data.GetProperty("round").GetInt32(); error = null;
                    foreach (var n in seed.Workflow.Nodes.Where(n => n.Kind is "fanout" or "build" or "test" or "security" or "quality" or "gate" or "report" or "release" || n.Id == "final-approval")) nodes[n.Id] = new(n.Id, "pending");
                    break;
            }
        }
        var latestJobs = jobs.Values.Where(j => j.Round == round).ToArray();
        string State()
        {
            if (control is not null) return control;
            if (nodes["release"].Status is "passed" or "skipped_satisfied") return "completed";
            if (nodes["final-approval"].Status == "approved") return "awaiting_release";
            if (nodes["final-approval"].Status == "waiting_approval") return "awaiting_final_approval";
            if (nodes["approval-plan"].Status == "waiting_approval") return "awaiting_plan_approval";
            if (latestJobs.Any(j => j.Status == "running")) return "running";
            if (latestJobs.Any(j => j.Status == "queued")) return "queued";
            if (latestJobs.Any(j => j.Status == "waiting_cloud_approval")) return "waiting_cloud_approval";
            if (latestJobs.Any(j => j.Status == "waiting_input")) return "waiting_input";
            return nodes.Values.Any(n => n.Status is "failed" or "blocked") || error is not null ? "blocked" : "queued";
        }
        var currentReceipts = receipts.Where(r => jobs.Values.Any(j => j.JobId == r.JobId && j.Round == round && j.Status != "superseded" && (r.Attempt == 0 || r.Attempt == j.Attempts))).ToArray();
        var findings = currentReceipts.SelectMany(r => r.Findings).ToList();
        if (error is not null) findings.Add(new("runtime", "high", error));
        var state = State();
        var status = state == "completed" ? "completed" : nodes["gate"].Status == "passed" ? "ready_for_owner" : nodes["gate"].Status == "failed" || state == "blocked" ? "blocked" : "in_progress";
        var report = new SoftwareReport(status, new[] { spec is null ? null : "spec@" + RuntimeJson.Hash(spec), plan is null ? null : "plan@" + RuntimeJson.Hash(plan), candidate?.Sha256 }.Where(s => s is not null).Cast<string>().ToArray(),
            currentReceipts.Select(r => r.ReceiptId).ToArray(), findings.ToArray(), nodes.Values.Where(n => n.Status is "waiting_approval" or "waiting_cloud_approval").Select(n => n.Id).ToArray(),
            jobs.Values.Any(j => j.ModelCalls > 0 && (j.UnreportedCalls > 0 || j.InputTokens is null)) ? null : jobs.Values.Sum(j => j.InputTokens ?? 0),
            jobs.Values.Any(j => j.ModelCalls > 0 && (j.UnreportedCalls > 0 || j.OutputTokens is null)) ? null : jobs.Values.Sum(j => j.OutputTokens ?? 0),
            jobs.Values.Sum(j => j.ModelCalls), ["Model statements are proposals; only recorded command/check receipts can satisfy tool gates.", "Dependency advisories and semantic/legal acceptance require the configured evidence and Owner review; unknown usage/cost is not zero."]);
        return new(seed, state, round, spec, plan, nodes.Values.ToArray(), jobs.Values.ToArray(), patches.ToArray(), candidate, receipts.ToArray(), grant, report,
            events.Max(e => e.Seq), resume, error, release, feedback.ToArray(), stepMode, handoffs.ToArray(), acks.ToArray());
    }
    public async Task<SoftwareRun?> GetSoftwareRunAsync(string company, string id, CancellationToken ct)
    {
        var events = await ReadEventsAsync(company, 0, ct); return RebuildSoftwareRun(company, id, events.Where(e => e.RunId == id).ToArray());
    }
    public async Task<SoftwareRun> CreateSoftwareRunAsync(string company, SoftwareCreateRequest request, int projectVersion, RepositorySnapshot snapshot, LocalModel[] models, string key, CancellationToken ct)
    {
        if (!Identifiers.IsValid(request.ProjectId) || string.IsNullOrWhiteSpace(request.Objective) || request.Objective.Length > 4000 || SourceSafety.ContainsKnownSecret(request.Objective) ||
            request.SourceIds is { Length: > 12 } || request.Assignments?.Any(a => !SoftwareContracts.Roles.Contains(a.Key) || !Identifiers.IsValid(a.Value)) == true) throw new ArgumentException("Invalid software request, role assignments or selected sources.");
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(company, 0, ct); var hash = RuntimeJson.Hash(request);
            var prior = events.FirstOrDefault(e => e.Type == "software.create.command" && ReadString(e.Data, "key") == key);
            if (prior is not null) return ReadString(prior.Data, "hash") == hash ? RebuildSoftwareRun(company, prior.RunId, events.Where(e => e.RunId == prior.RunId).ToArray())! : throw new WorkspaceConflictException("Create key belongs to another software request.");
            var project = ProjectsFrom(events).FirstOrDefault(p => p.ProjectId == request.ProjectId) ?? throw new KeyNotFoundException();
            if (project.Version != projectVersion) throw new WorkspaceConflictException("Project changed while its repository snapshot was being prepared.");
            var workflow = WorkflowsFrom(events).FirstOrDefault(w => w.Name == request.WorkflowName && w.Version == request.WorkflowVersion) ?? throw new ArgumentException("Workflow version not installed.");
            var compilation = SoftwareContracts.Compile(workflow); if (!compilation.Valid) throw new ArgumentException(string.Join(' ', compilation.Errors));
            var agents = AgentsFrom(company, await ReadEventsUnsafeAsync(CatalogCompany, 0, ct), events); var configurations = ConfigurationsFrom(events); var skills = SkillsFrom(events);
            var bindings = new List<RoleBinding>();
            foreach (var role in SoftwareContracts.Roles)
            {
                var selected = request.Assignments?.GetValueOrDefault(role) ?? role + "-agent";
                var members = role == "dev" ? agents.Where(a => a.RoleIds.Contains(role)).ToArray() : agents.Where(a => a.AgentId == selected && a.RoleIds.Contains(role)).ToArray();
                if (!members.Any(a => role == "dev" || a.AgentId == selected) || role == "dev" && !members.Any(a => a.AgentId == selected)) throw new ArgumentException("Every role needs an assigned company agent.");
                foreach (var agent in members)
                {
                    var configuration = configurations.FirstOrDefault(c => c.AgentId == agent.AgentId) ?? new(agent.AgentId, 0, "Unassigned", null, [], 8000, 4096);
                    var packages = configuration.SkillRefs.Select(r => skills.Single(s => s.SkillId + "@" + s.Version == r)).Where(s => s.Roles.Contains(role)).ToArray();
                    var cloud = configuration.ProviderId is null ? null : ResolveAgentCloud(configuration, events);
                    var model = cloud is not null ? new LocalModel(configuration.ModelId!, RuntimeJson.Hash(cloud), "cloud route", DateTimeOffset.UtcNow) : configuration.ModelId is null || role is not ("ba" or "leader" or "dev") ? null : models.FirstOrDefault(m => m.Name == configuration.ModelId) ?? throw new ArgumentException("An assigned model is not installed locally.");
                    bindings.Add(new(role, agent, configuration, packages, model, cloud));
                }
            }
            var memory = CanonicalFrom(events); var sources = SourcesFrom(events);
            foreach (var record in memory) { ValidateCitations(record.Citations, sources); if (record.Citations.Any(c => sources.Where(s => s.SourceId == c.SourceId).Max(s => s.Revision) != c.Revision)) throw new WorkspaceConflictException("Review stale canonical source citations before creating a software run."); }
            var selectedSources = (request.SourceIds ?? []).Distinct().Select(id => sources.LastOrDefault(s => s.SourceId == id) ?? throw new ArgumentException("Source does not resolve in this company."))
                .Select(s => new SourceExcerpt(s.SourceId, s.Revision, s.Title, s.Content, new(s.SourceId, s.Revision, 0, s.Content.Length, s.ContentSha256))).ToArray();
            var id = "work-" + Guid.NewGuid().ToString("N");
            var seed = new SoftwareSeed(id, company, request.Objective.Trim(), project, snapshot, workflow, bindings.ToArray(), memory, selectedSources, request.ReleaseRequested, DateTimeOffset.UtcNow);
            await AppendUnsafeAsync(company, id, [DomainEvent.Create("software.created", seed), DomainEvent.Create("software.create.command", new { key, hash }), DomainEvent.Create("software.node.changed", new SoftwareNodeState("intake", "passed"))], events, ct);
            await AdvanceSoftwareUnsafeAsync(company, id, events, ct);
            return RebuildSoftwareRun(company, id, events.Where(e => e.RunId == id).ToArray())!;
        }
        finally { _gate.Release(); }
    }
    private static bool LogicalDone(string status) => status is "passed" or "produced" or "approved" or "skipped_satisfied";
    private static bool TerminalNode(string status) => LogicalDone(status) || status is "failed" or "blocked" or "not_run";
    private static RoleBinding Binding(SoftwareRun run, string role, string? agent = null) => run.Seed.Bindings.First(b => b.RoleId == role && (agent is null || b.Agent.AgentId == agent));
    private static SoftwareNodeState Node(SoftwareRun run, string id) => run.Nodes.FirstOrDefault(n => n.Id == id) ?? new(id, "pending");
    private static DomainEvent NodeEvent(string id, string status, string? error = null) => DomainEvent.Create("software.node.changed", new SoftwareNodeState(id, status, error));
    private async Task QueueSoftwareUnsafeAsync(SoftwareRun run, string node, string role, string? taskId, List<DomainEvent> events, CancellationToken ct)
    {
        if (run.Jobs.Any(j => j.NodeId == node && j.Round == run.Round && j.Status != "superseded")) return;
        var agent = taskId is null ? Binding(run, role) : Binding(run, role, run.Plan!.Tasks.Single(t => t.Id == taskId).AgentId);
        string input;
        try { input = SoftwareContext.Build(run, role, taskId, node); }
        catch (WorkspaceConflictException)
        {
            await AppendUnsafeAsync(run.Seed.CompanyId, run.Seed.RunId, [NodeEvent(node, "blocked", "CONTEXT_BUDGET_EXCEEDED"), DomainEvent.Create("software.error", new { code = "CONTEXT_BUDGET_EXCEEDED" })], events, ct); return;
        }
        var modelNode = node is "ba" or "leader" || taskId is not null;
        var job = new SoftwareJob("job-" + Guid.NewGuid().ToString("N"), run.Seed.RunId, run.Seed.CompanyId, node, role, agent.Agent.AgentId, run.Round, taskId,
            input, RuntimeJson.TextHash(input), modelNode && agent.Cloud is not null ? "waiting_cloud_approval" : modelNode && agent.Model is null ? "waiting_input" : "queued");
        await AppendUnsafeAsync(run.Seed.CompanyId, run.Seed.RunId, [DomainEvent.Create("software.handoff.created", BuildSoftwareHandoff(run, job)), DomainEvent.Create("software.job.changed", job), NodeEvent(node, job.Status)], events, ct);
    }
    private async Task AdvanceSoftwareUnsafeAsync(string company, string id, List<DomainEvent> events, CancellationToken ct)
    {
        for (var iteration = 0; iteration < 40; iteration++)
        {
            var run = RebuildSoftwareRun(company, id, events.Where(e => e.RunId == id).ToArray())!;
            if (run.State is "paused" or "cancelled" or "completed" or "awaiting_release") return;
            var changed = false;
            async Task Add(params DomainEvent[] additions) { await AppendUnsafeAsync(company, id, additions, events, ct); changed = true; }
            if (run.Error is not null && Node(run, "subpm").Status == "pending") await Add(SoftwareReportEvents(run, events));
            foreach (var definition in run.Seed.Workflow.Nodes)
            {
                if (Node(run, definition.Id).Status != "pending") continue;
                if (definition.Id == "final-approval" && Node(run, "gate").Status != "passed") continue;
                var deps = definition.DependsOn.Select(d => Node(run, d)).ToArray();
                if (!deps.All(d => LogicalDone(d.Status)) && !(definition.Kind is "gate" or "report" or "security" or "quality" && deps.All(d => TerminalNode(d.Status)))) continue;
                if (definition.When == "release_requested" && !run.Seed.ReleaseRequested) { await Add(NodeEvent(definition.Id, "skipped_satisfied")); continue; }
                switch (definition.Kind)
                {
                    case "agent": await QueueSoftwareUnsafeAsync(run, definition.Id, definition.Role!, null, events, ct); changed = true; break;
                    case "approval": await Add(NodeEvent(definition.Id, "waiting_approval")); break;
                    case "fanout":
                        if (run.Plan is null) break;
                        foreach (var task in run.Plan.Tasks.Where(t => t.DependsOn.All(dep => run.Patches.Any(p => p.Round == run.Round && p.TaskId == dep))))
                            if (!run.Jobs.Any(j => j.NodeId == "dev-" + task.Id && j.Round == run.Round)) { await QueueSoftwareUnsafeAsync(run, "dev-" + task.Id, "dev", task.Id, events, ct); changed = true; }
                        if (run.Plan.Tasks.All(t => run.Patches.Any(p => p.Round == run.Round && p.TaskId == t.Id)))
                        {
                            try
                            {
                                var candidate = SoftwareContext.Integrate(run);
                                await Add(DomainEvent.Create("software.candidate.produced", candidate), NodeEvent(definition.Id, "produced"));
                            }
                            catch (WorkspaceConflictException error) { await Add(NodeEvent(definition.Id, "failed", error.Message), DomainEvent.Create("software.error", new { code = "PATCH_INTEGRATION_CONFLICT" })); }
                        }
                        break;
                    case "build": case "test": case "security": case "quality":
                        if (run.Candidate is not null) { await QueueSoftwareUnsafeAsync(run, definition.Id, definition.Role!, null, events, ct); changed = true; }
                        break;
                    case "gate":
                        var relevant = SoftwareChecks.CurrentReceipts(run);
                        var passed = SoftwareChecks.GatesPass(run);
                        await Add(NodeEvent(definition.Id, passed ? "passed" : "failed"));
                        break;
                    case "report":
                        await Add(SoftwareReportEvents(run, events));
                        if (Node(run, "gate").Status == "failed" && run.Candidate is not null && SoftwareChecks.CanRepair(run) && run.Round < run.Seed.Workflow.Nodes.Single(n => n.Id == "gate").MaxLoop)
                            await Add(DomainEvent.Create("software.round.started", new { round = run.Round + 1 }));
                        break;
                    case "release": break; // Explicit Owner release endpoint performs and reconciles Git side effects.
                }
            }
            // A failed build still yields independent scans and an explicit not-run test receipt.
            if (Node(run, "build").Status is "failed" or "blocked" && Node(run, "tester").Status == "pending")
            {
                var receipt = SoftwareChecks.NotRun(run, "tester", "Build did not pass; test command was not executed.");
                var notRun = new SoftwareJob(receipt.JobId, id, company, "tester", "tester", receipt.AgentId, run.Round, null, "", "", "failed", Error: "BUILD_PREREQUISITE_FAILED");
                await Add(DomainEvent.Create("software.job.changed", notRun), DomainEvent.Create("software.receipt.recorded", receipt), NodeEvent("tester", "not_run"));
            }
            // Report a failed model/patch even when no candidate can be built. Never silently claim downstream PASS.
            if ((run.Candidate is null || run.Candidate.Round != run.Round) && run.Jobs.Any(j => j.Round == run.Round && j.Status is "failed" or "interrupted") && !run.Jobs.Any(j => j.Round == run.Round && j.Status is "queued" or "running"))
            {
                if (Node(run, "subpm").Status == "pending") await Add(SoftwareReportEvents(run, events));
            }
            if (!changed) return;
        }
    }
    public async Task<SoftwareJob?> ClaimSoftwareJobAsync(string worker, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            foreach (var company in CompaniesFrom(await ReadEventsUnsafeAsync(CatalogCompany, 0, ct)))
            {
                var events = await ReadEventsUnsafeAsync(company.CompanyId, 0, ct);
                foreach (var current in SoftwareRunsFrom(company.CompanyId, events))
                {
                    if (current.State is "paused" or "cancelled" or "completed" or "awaiting_release") continue;
                    await AdvanceSoftwareUnsafeAsync(company.CompanyId, current.Seed.RunId, events, ct);
                    var run = RebuildSoftwareRun(company.CompanyId, current.Seed.RunId, events.Where(e => e.RunId == current.Seed.RunId).ToArray())!;
                    foreach (var expired in run.Jobs.Where(j => j.Status == "running" && j.LeaseUntil <= DateTimeOffset.UtcNow))
                        await AppendUnsafeAsync(company.CompanyId, current.Seed.RunId, [DomainEvent.Create("software.job.changed", expired with { Status = "interrupted", LeaseToken = null, Error = "LEASE_EXPIRED_REQUIRES_RETRY" }), NodeEvent(expired.NodeId, "blocked", "LEASE_EXPIRED_REQUIRES_RETRY")], events, ct);
                    run = RebuildSoftwareRun(company.CompanyId, current.Seed.RunId, events.Where(e => e.RunId == current.Seed.RunId).ToArray())!;
                    foreach (var job in run.Jobs.Where(j => j.Round == run.Round && j.Status == "queued"))
                    {
                        if (run.Jobs.Count(j => j.Status == "running") >= (run.StepMode ? 1 : run.Seed.Workflow.MaxParallel)) continue;
                        if (DateTimeOffset.UtcNow - run.Seed.CreatedAt > TimeSpan.FromMinutes(run.Seed.Workflow.MaxElapsedMinutes) || job.Attempts >= run.Seed.Workflow.MaxAttempts ||
                            (job.TaskId is not null || job.NodeId is "ba" or "leader") && run.Jobs.Sum(j => j.ModelCalls * Binding(run, j.RoleId, j.AgentId).Configuration.MaxOutputTokens) + Binding(run, job.RoleId, job.AgentId).Configuration.MaxOutputTokens > run.Seed.Workflow.MaxOutputTokens)
                        {
                            await AppendUnsafeAsync(company.CompanyId, run.Seed.RunId, [DomainEvent.Create("software.job.changed", job with { Status = "failed", Error = "RUN_BUDGET_EXHAUSTED" }), NodeEvent(job.NodeId, "blocked", "RUN_BUDGET_EXHAUSTED")], events, ct); continue;
                        }
                        var claimed = job with { Status = "running", Attempts = job.Attempts + 1, WorkerId = worker, LeaseToken = Guid.NewGuid().ToString("N"), LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(30) };
                        await AppendUnsafeAsync(company.CompanyId, run.Seed.RunId, [AcknowledgeSoftwareContext(run, claimed), DomainEvent.Create("software.job.changed", claimed), NodeEvent(job.NodeId, "running")], events, ct); return claimed;
                    }
                }
            }
            return null;
        }
        finally { _gate.Release(); }
    }
    public async Task<bool> HeartbeatSoftwareJobAsync(SoftwareJob claim, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(claim.CompanyId, 0, ct); var run = RebuildSoftwareRun(claim.CompanyId, claim.RunId, events.Where(e => e.RunId == claim.RunId).ToArray());
            var job = run?.Jobs.FirstOrDefault(j => j.JobId == claim.JobId);
            if (run is null || run.State is "paused" or "cancelled" || job?.Status != "running" || job.LeaseToken != claim.LeaseToken || job.LeaseUntil <= DateTimeOffset.UtcNow) return false;
            await AppendUnsafeAsync(claim.CompanyId, claim.RunId, [DomainEvent.Create("software.job.heartbeat", new { jobId = job.JobId, leaseUntil = DateTimeOffset.UtcNow.AddSeconds(30) })], events, ct); return true;
        }
        finally { _gate.Release(); }
    }
    public async Task<bool> CompleteSoftwareJobAsync(SoftwareJob claim, object? result, ModelUsage? usage, string? error, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(claim.CompanyId, 0, ct); var run = RebuildSoftwareRun(claim.CompanyId, claim.RunId, events.Where(e => e.RunId == claim.RunId).ToArray())!;
            var job = run.Jobs.FirstOrDefault(j => j.JobId == claim.JobId);
            if (run.State is "paused" or "cancelled" || job?.Status != "running" || job.LeaseToken != claim.LeaseToken || job.LeaseUntil <= DateTimeOffset.UtcNow) return false;
            var additions = new List<DomainEvent>(); var status = "produced";
            if (error is not null || result is null) { status = "failed"; additions.Add(NodeEvent(job.NodeId, "failed", error ?? "NO_RESULT")); }
            else
            {
                switch (result)
                {
                    case RequirementSpec spec: SoftwareContracts.ValidateSpec(spec); additions.Add(DomainEvent.Create("software.spec.produced", spec)); break;
                    case TaskGraph plan: SoftwareContracts.ValidatePlan(plan, run.Spec!, run.Seed.Project, run.Seed.Bindings); additions.Add(DomainEvent.Create("software.plan.produced", plan)); break;
                    case TaskPatch patch:
                        if (patch.Patch.Files.Any(f => RepositoryService.TouchesOmitted(f.Path, run.Seed.Repository.OmittedPaths))) throw new InvalidDataException("A denied base path cannot become a new text patch.");
                        if (job.TaskId != patch.TaskId || patch.JobId != job.JobId || patch.Round != job.Round || patch.Sha256 != RuntimeJson.Hash(patch.Patch)) throw new InvalidDataException("Patch receipt does not match the owned job.");
                        RepositoryService.Apply(SoftwareContext.TaskBaseline(run, patch.TaskId), patch.Patch, run.Plan!.Tasks.Single(t => t.Id == patch.TaskId).AllowedPaths);
                        if (patch.Receipt is null || patch.Receipt.JobId != job.JobId || patch.Receipt.Tool != "worktree.patch" || patch.Receipt.InputSha256 != patch.Sha256 || patch.Receipt.ExitCode != 0) throw new InvalidDataException("Dev output requires a matching real worktree receipt.");
                        additions.Add(DomainEvent.Create("software.receipt.recorded", patch.Receipt));
                        additions.Add(DomainEvent.Create("software.patch.applied", patch)); break;
                    case ExecutionReceipt receipt:
                        if (receipt.JobId != job.JobId || receipt.InputSha256 != run.Candidate?.Sha256 || receipt.RoleId != job.RoleId || receipt.AgentId != job.AgentId) throw new InvalidDataException("Execution receipt does not bind the current owned candidate.");
                        additions.Add(DomainEvent.Create("software.receipt.recorded", receipt)); status = receipt.Status == "executed" && receipt.ExitCode == 0 && !receipt.Findings.Any(f => f.Severity is "critical" or "high") ? "passed" : "failed"; break;
                    default: throw new ArgumentException("Unsupported typed node output.");
                }
                additions.Add(NodeEvent(job.NodeId, status));
            }
            additions.Add(DomainEvent.Create("software.job.changed", job with { Status = status == "failed" ? "failed" : "completed", LeaseToken = null, LeaseUntil = null, Error = error,
                InputTokens = job.ModelCalls > 0 ? job.InputTokens : usage?.InputTokens, OutputTokens = job.ModelCalls > 0 ? job.OutputTokens : usage?.OutputTokens, OutputSha256 = result is null ? null : RuntimeJson.Hash(result) }));
            additions.Add(DomainEvent.Create("interaction.created", new PublicInteraction("message-" + Guid.NewGuid().ToString("N"), job.RunId, status == "failed" ? "escalation" : "status", job.AgentId,
                ["owner"], status == "failed" ? "Node " + job.NodeId + " failed; inspect the persisted findings and receipts." : "Node " + job.NodeId + " produced its typed result.",
                result is null ? [] : [RuntimeJson.Hash(result)], [], null, "recorded", "node-" + job.JobId + "-" + job.Attempts, DateTimeOffset.UtcNow, "runtime")));
            if (run.StepMode) additions.Add(DomainEvent.Create("software.paused", new { resumeState = "checkpoint" }));
            await AppendUnsafeAsync(claim.CompanyId, claim.RunId, additions, events, ct);
            await AdvanceSoftwareUnsafeAsync(claim.CompanyId, claim.RunId, events, ct); return true;
        }
        finally { _gate.Release(); }
    }
}
