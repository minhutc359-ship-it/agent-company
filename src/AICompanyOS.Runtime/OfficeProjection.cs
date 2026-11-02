namespace AICompanyOS.Runtime;

public sealed record OfficeAgent(Agent Agent, string State, string? RunId, string Summary, long Sequence, DateTimeOffset? LastEventAt, string? ModelName = null, string? ActiveRoleId = null);
public sealed record OfficeSnapshot(long Cursor, OfficeAgent[] Agents, int RunCount);

public sealed partial class FileEventStore
{
    public async Task<OfficeSnapshot> OfficeSnapshotAsync(string companyId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(companyId, 0, ct);
            var agents = AgentsFrom(companyId, await ReadEventsUnsafeAsync(CatalogCompany, 0, ct), events);
            return ProjectOffice(companyId, agents, events);

        }
        finally { _gate.Release(); }
    }
    private static OfficeSnapshot ProjectOffice(string companyId, Agent[] agents, List<DomainEvent> events)
    {
        var runs = events.GroupBy(e => e.RunId).Select(g => RebuildRun(companyId, g.Key, g.ToArray()))
            .Where(r => r is not null).Cast<WorkflowRun>().OrderByDescending(r => r.LastSequence).ToArray();
        var projection = agents.Select(agent =>
        {
            var related = runs.Where(r => r.CurrentOwner == agent.AgentId || r.LeaderAgentId == agent.AgentId && r.State == "awaiting_handoff" ||
                r.Delivery?.Bindings.Any(b => b.Agent.AgentId == agent.AgentId) == true).ToArray();
            var run = related.OrderBy(r => r.State is "approved" or "revision_requested" or "cancelled" or "delivered" ? 1 : 0).FirstOrDefault();
            if (run is null) return new OfficeAgent(agent, "idle", null, "No active invocation. Local models are selected per run.", 0, null);
            var state = run.State switch
            {
                "awaiting_handoff" => "handoff_pending",
                "awaiting_approval" => "waiting_approval",
                "queued" => "queued",
                "running" => "working",
                "blocked" => "blocked",
                "delivery_blocked" => "blocked",
                "delivery_revision_requested" => "blocked",
                "awaiting_delivery_approval" => "waiting_approval",
                "awaiting_delivery_input" => "waiting_input",
                "paused" => "paused",
                "revision_requested" => "blocked",
                _ => "idle"
            };
            var summary = run.State switch
            {
                "approved" => "Owner approved the plan. No implementation executed.",
                "delivered" => "Owner accepted this static document package. No publish or repository action executed.",
                "delivery_blocked" => "Static-document checks blocked this artifact. Inspect the saved receipts.",
                "delivery_revision_requested" => "Owner requested a revised static artifact.",
                "awaiting_delivery_input" => "Waiting for Owner-supplied static files. No model or shell is active.",
                "awaiting_delivery_approval" => "Static-document receipts are complete. Owner acceptance and rights review remain required.",
                "cancelled" => "Run cancelled. Artifacts retained in the event log.",
                "queued" => "Local inference queued with pinned context. No tools granted.",
                "running" => "A leased local model invocation is active. Output awaits validation.",
                "blocked" => "Local inference blocked: " + run.LastError,
                "paused" => "Workflow paused at its persisted checkpoint.",
                "awaiting_handoff" => "Spec recorded; waiting for an Owner-mediated ACK.",
                "awaiting_approval" => "Plan recorded; waiting for Owner approval.",
                _ => "Owner requested a revision. Start a revised run."
            };
            var invocation = run.Invocations?.LastOrDefault(j => j.AgentId == agent.AgentId);
            var receipt = run.Delivery?.Receipts?.LastOrDefault(r => r.AgentId == agent.AgentId);
            if (receipt is not null) summary += $" Last static check: {receipt.RoleId}, exit {receipt.ExitCode}, receipt {receipt.ReceiptId}.";
            return new OfficeAgent(agent, state, run.RunId, summary, run.LastSequence,
                events.FirstOrDefault(e => e.Seq == run.LastSequence)?.CreatedAt, invocation?.Model.Name, receipt?.RoleId ?? invocation?.RoleId);
        }).ToArray();
        return new(events.LastOrDefault()?.Seq ?? 0, projection, runs.Length);
    }

    public async Task<WorkspaceView> WorkspaceSnapshotAsync(string companyId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(companyId, 0, ct);
            var agents = AgentsFrom(companyId, await ReadEventsUnsafeAsync(CatalogCompany, 0, ct), events);
            var runs = events.GroupBy(e => e.RunId).Select(g => RebuildRun(companyId, g.Key, g.ToArray()))
                .Where(r => r is not null).Cast<WorkflowRun>().OrderByDescending(r => r.LastSequence).ToArray();
            var sources = SourcesFrom(events).GroupBy(s => s.SourceId).Select(g => g.Last())
                .Select(s => new SourceSummary(s.SourceId, s.Revision, s.Title, s.ContentSha256, s.ImportedAt, s.Content.Length)).ToArray();
            return new(Backend, runs, agents, events.Select(e => new EventSummary(e.Seq, e.EventId, e.RunId, e.Type, e.CreatedAt)).ToArray(),
                sources, CanonicalFrom(events), ProposalsFrom(events), ProjectOffice(companyId, agents, events));
        }
        finally { _gate.Release(); }
    }

}

public sealed record EventSummary(long Seq, string EventId, string RunId, string Type, DateTimeOffset CreatedAt);
public sealed record SourceSummary(string SourceId, int Revision, string Title, string ContentSha256, DateTimeOffset ImportedAt, int Characters);
public sealed record WorkspaceView(string Storage, WorkflowRun[] Runs, Agent[] Agents, EventSummary[] Events,
    SourceSummary[] Sources, MemoryRecord[] Canonical, MemoryProposal[] Proposals, OfficeSnapshot Office);
