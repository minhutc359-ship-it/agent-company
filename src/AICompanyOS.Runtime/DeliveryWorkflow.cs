using System.Text.Json;

namespace AICompanyOS.Runtime;

public sealed partial class FileEventStore
{
    private static DeliverySession? DeliveryFrom(IEnumerable<DomainEvent> events)
    {
        DeliverySession? session = null;
        foreach (var e in events)
        {
            if (e.Type == "delivery.prepared") session = e.Data.Deserialize<DeliverySession>(JsonOptions);
            else if (session is not null)
            {
                if (e.Type == "delivery.artifact.produced") session = session with
                { Artifact = e.Data.Deserialize<DeliveryArtifact>(JsonOptions), Receipts = [], Report = null, OwnerAccepted = null, AcceptanceConfirmed = false, RightsConfirmed = false };
                else if (e.Type == "delivery.check.executed") session = session with { Receipts = [.. session.Receipts ?? [], e.Data.Deserialize<CheckReceipt>(JsonOptions)!] };
                else if (e.Type == "delivery.report.produced") session = session with { Report = e.Data.Deserialize<DeliverySummary>(JsonOptions) };
                else if (e.Type == "delivery.reviewed") session = session with
                { OwnerAccepted = e.Data.GetProperty("approved").GetBoolean(), AcceptanceConfirmed = e.Data.GetProperty("acceptanceConfirmed").GetBoolean(), RightsConfirmed = e.Data.GetProperty("rightsConfirmed").GetBoolean(),
                    Report = session.Report! with { Status = e.Data.GetProperty("approved").GetBoolean() ? "owner_accepted" : "revision_requested", RemainingReview = e.Data.GetProperty("approved").GetBoolean() ? [] : ["Owner requested a revision."] } };
            }
        }
        return session;
    }
    private static CommandResult? DeliveryReplay(WorkflowRun run, IReadOnlyList<DomainEvent> events, string key, string hash)
    {
        var prior = events.FirstOrDefault(e => e.Type == "delivery.command" && ReadString(e.Data, "key") == key);
        return prior is null ? null : new(ReadString(prior.Data, "hash") == hash ? CommandOutcome.AlreadyApplied : CommandOutcome.Conflict, run, "Idempotency key belongs to a different delivery command.");
    }
    public async Task<CommandResult> PrepareDeliveryAsync(string companyId, string runId, DeliveryPrepareRequest request, string key, CancellationToken ct)
    {
        DeliveryTools.ValidateCriteria(request.Criteria);
        if (request.Mode is not ("manual" or "local_model") || request.Assignments?.Any(p => !DeliveryTools.ReviewRoles.Contains(p.Key) || !Identifiers.IsValid(p.Value)) == true)
            throw new ArgumentException("Choose manual or local_model and valid delivery role assignments.");
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(companyId, 0, ct);
            var runEvents = events.Where(e => e.RunId == runId).ToArray();
            var run = RebuildRun(companyId, runId, runEvents);
            if (run is null) return new(CommandOutcome.NotFound, null);
            var hash = RuntimeJson.Hash(new { operation = "prepare", request });
            var replay = DeliveryReplay(run, runEvents, key, hash); if (replay is not null) return replay;
            if (run.LastSequence != request.ExpectedSequence || run.State != "approved" || run.Delivery is not null)
                return new(CommandOutcome.Conflict, run, "Prepare delivery once from a current Owner-approved plan.");
            if (request.Mode == "local_model" && run.ExecutionMode != "local_model")
                return new(CommandOutcome.Conflict, run, "AI delivery requires a plan generated with a pinned local model.");
            var agents = AgentsFrom(companyId, await ReadEventsUnsafeAsync(CatalogCompany, 0, ct), events);
            var bindings = DeliveryTools.ReviewRoles.Select(role => new DeliveryBinding(role, agents.FirstOrDefault(a =>
                a.AgentId == (request.Assignments?.GetValueOrDefault(role) ?? role + "-agent") && a.RoleIds.Contains(role))
                ?? throw new WorkspaceConflictException("Every delivery role must have an assigned agent in this company."))).ToArray();
            var session = new DeliverySession(DeliveryTools.WorkflowVersion, request.Mode, bindings, request.Criteria);
            var additions = new List<DomainEvent> { DomainEvent.Create("delivery.prepared", session) };
            if (request.Mode == "local_model")
            {
                var ba = JobsFrom(runEvents).Single(j => j.RoleId == "ba" && j.Status == "completed");
                var prior = JobsFrom(runEvents).Where(j => j.RoleId == "leader" && j.Status == "completed")
                    .Select(j => new PriorRoleOutput(j.JobId, j.RoleId, j.OutputSha256!, j.Output!)).ToArray();
                var dev = MakeJob(companyId, runId, run.Objective, bindings.Single(b => b.RoleId == "dev").Agent, "dev", ba.Model,
                    ba.Context.CanonicalRecords, ba.Context.Sources, prior, ba.Context.BudgetWarnings,
                    new(DeliveryTools.WorkflowVersion, run.Plan!.Steps, request.Criteria));
                additions.Add(DomainEvent.Create("invocation.queued", dev));
            }
            additions.Add(DomainEvent.Create("delivery.command", new { key, hash }));
            await AppendUnsafeAsync(companyId, runId, additions, events, ct);
            return new(CommandOutcome.Applied, RebuildRun(companyId, runId, events.Where(e => e.RunId == runId).ToArray()));
        }
        finally { _gate.Release(); }
    }
    public async Task<CommandResult> SubmitArtifactAsync(string companyId, string runId, ArtifactSubmitRequest request, string key, CancellationToken ct)
    {
        DeliveryTools.ValidateDraft(request.Draft);
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(companyId, 0, ct);
            var runEvents = events.Where(e => e.RunId == runId).ToArray();
            var run = RebuildRun(companyId, runId, runEvents);
            if (run is null) return new(CommandOutcome.NotFound, null);
            var hash = RuntimeJson.Hash(new { operation = "submit", request });
            var replay = DeliveryReplay(run, runEvents, key, hash); if (replay is not null) return replay;
            if (run.LastSequence != request.ExpectedSequence || run.Delivery is null || run.State is not ("awaiting_delivery_input" or "delivery_blocked" or "delivery_revision_requested") ||
                run.Delivery.Artifact?.Version >= DeliveryTools.MaximumVersions)
                return new(CommandOutcome.Conflict, run, "Reload a delivery input/revision checkpoint; at most three artifact versions are supported.");
            var additions = CheckArtifactEvents(run.Delivery, request.Draft, "owner_supplied", null);
            additions.Add(DomainEvent.Create("delivery.command", new { key, hash }));
            await AppendUnsafeAsync(companyId, runId, additions, events, ct);
            return new(CommandOutcome.Applied, RebuildRun(companyId, runId, events.Where(e => e.RunId == runId).ToArray()));
        }
        finally { _gate.Release(); }
    }
    private static List<DomainEvent> CheckArtifactEvents(DeliverySession session, ArtifactDraft draft, string origin, string? jobId)
    {
        var artifact = DeliveryTools.Produce(draft, (session.Artifact?.Version ?? 0) + 1, origin, jobId);
        var receipts = DeliveryTools.Check(artifact, session);
        var checkedSession = session with { Artifact = artifact, Receipts = receipts };
        return [DomainEvent.Create("delivery.artifact.produced", artifact),
            .. receipts.Select(r => DomainEvent.Create("delivery.check.executed", r)),
            DomainEvent.Create("delivery.report.produced", DeliveryTools.Summarize(checkedSession))];
    }
    public async Task<CommandResult> ReviewDeliveryAsync(string companyId, string runId, DeliveryReviewRequest request, string key, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(companyId, 0, ct);
            var runEvents = events.Where(e => e.RunId == runId).ToArray();
            var run = RebuildRun(companyId, runId, runEvents);
            if (run is null) return new(CommandOutcome.NotFound, null);
            var hash = RuntimeJson.Hash(new { operation = "review", request });
            var replay = DeliveryReplay(run, runEvents, key, hash); if (replay is not null) return replay;
            if (run.LastSequence != request.ExpectedSequence || run.State != "awaiting_delivery_approval" || run.Delivery?.Artifact?.Sha256 != request.ArtifactSha256 ||
                !DeliveryTools.GatesPass(run.Delivery) || request.Approved && (!request.AcceptanceConfirmed || !request.RightsConfirmed))
                return new(CommandOutcome.Conflict, run, "Review the current passing artifact and confirm acceptance and rights before approving export.");
            await AppendUnsafeAsync(companyId, runId,
                [DomainEvent.Create("delivery.reviewed", new { request.Approved, request.AcceptanceConfirmed, request.RightsConfirmed, request.ArtifactSha256 }),
                 DomainEvent.Create("delivery.command", new { key, hash })], events, ct);
            return new(CommandOutcome.Applied, RebuildRun(companyId, runId, events.Where(e => e.RunId == runId).ToArray()));
        }
        finally { _gate.Release(); }
    }
}
