using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace AICompanyOS.Runtime;

public sealed partial class FileEventStore : IDisposable
{
    private bool _maintenance;
    public bool Maintenance => Volatile.Read(ref _maintenance);
    public async Task SetMaintenanceAsync(bool value, CancellationToken ct)
    { await _gate.WaitAsync(ct); try { _maintenance = value; } finally { _gate.Release(); } }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FileStream _writerLock;
    private IEventJournal _journal;
    private StorageProfile _profile;

    public FileEventStore(string root, string? backend = null)
    {
        _root = Path.GetFullPath(root);
        if (!Directory.Exists(_root))
        {
            Directory.CreateDirectory(_root);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        _writerLock = new FileStream(LocalFiles.Child(_root, "runtime.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            var pointer = LocalFiles.Child(_root, "storage.json");
            _profile = File.Exists(pointer)
                ? JsonSerializer.Deserialize<StorageProfile>(File.ReadAllBytes(pointer), JsonOptions) ?? throw new InvalidDataException("Invalid storage profile.")
                : new StorageProfile(backend ?? "file");
            if (backend is not null && backend != _profile.Backend) throw new InvalidOperationException("Use verified migration to switch existing storage.");
            _journal = OpenJournal(_profile);
            if (!File.Exists(pointer))
                LocalFiles.WriteAtomicAsync(pointer, JsonSerializer.SerializeToUtf8Bytes(_profile, JsonOptions), CancellationToken.None).GetAwaiter().GetResult();
        }
        catch { _journal?.Dispose(); _writerLock.Dispose(); throw; }
    }

    public string Backend => _journal.Backend;

    public void Dispose()
    {
        _journal.Dispose();
        _writerLock.Dispose();
        _gate.Dispose();
    }

    public async Task<CreateRunResult> CreateRunAsync(
        string companyId, string runId, string objective, string[] sourceRefs, string idempotencyKey, CancellationToken cancellationToken, string leaderAgentId = "leader-agent", string baAgentId = "ba-agent")
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var events = await ReadEventsUnsafeAsync(companyId, 0, cancellationToken);
            var requestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            {
                companyId,
                objective,
                sourceRefs
            }, JsonOptions))).ToLowerInvariant();
            if (leaderAgentId != "leader-agent" || baAgentId != "ba-agent")
                requestHash = RuntimeJson.Hash(new { companyId, objective, sourceRefs, leaderAgentId, baAgentId });
            var prior = events.FirstOrDefault(item => item.Type == "workflow.created" &&
                ReadString(item.Data, "idempotencyKey") == idempotencyKey);
            if (prior is not null)
            {
                var existingRunEvents = events.Where(item => item.RunId == prior.RunId).ToArray();
                var existingRun = RebuildRun(companyId, prior.RunId, existingRunEvents)
                    ?? throw new InvalidDataException("The idempotency key points to a missing workflow run.");
                if (!string.Equals(ReadString(prior.Data, "requestHash"), requestHash, StringComparison.Ordinal))
                    return new CreateRunResult(existingRun, false, true);
                var additions = new List<DomainEvent>();
                var existingSpec = existingRunEvents.FirstOrDefault(item => item.Type == "spec.produced")
                    ?.Data.Deserialize<SpecArtifact>(JsonOptions);
                var recoveredMemory = prior.Data.TryGetProperty("memorySnapshot", out var memory) ? memory.Deserialize<MemoryRecord[]>(JsonOptions) : [];
                var recoveredSpec = existingSpec ?? new SpecArtifact($"spec-{Guid.NewGuid():N}", objective, sourceRefs, "produced", recoveredMemory);
                if (existingSpec is null) additions.Add(DomainEvent.Create("spec.produced", recoveredSpec));
                if (!existingRunEvents.Any(item => item.Type == "handoff.requested"))
                    additions.Add(DomainEvent.Create("handoff.requested", HandoffPacket.Create(companyId, prior.RunId, recoveredSpec, leaderAgentId, baAgentId)));
                if (additions.Count > 0)
                    await AppendUnsafeAsync(companyId, prior.RunId, additions, events, cancellationToken);
                if (additions.Count > 0)
                    existingRun = RebuildRun(companyId, prior.RunId, events.Where(item => item.RunId == prior.RunId).ToArray())
                        ?? throw new InvalidDataException("Recovered workflow could not be rebuilt from its event records.");
                return new CreateRunResult(existingRun, false);
            }

            var catalog = await ReadEventsUnsafeAsync(CatalogCompany, 0, cancellationToken);
            Agent[] bindings = [];
            if (catalog.Any(e => e.RunId == companyId && e.Type == "company.created"))
            {
                var agents = AgentsFrom(companyId, catalog, events);
                var ba = agents.FirstOrDefault(a => a.AgentId == baAgentId && a.RoleIds.Contains("ba"));
                var leader = agents.FirstOrDefault(a => a.AgentId == leaderAgentId && a.RoleIds.Contains("leader"));
                if (ba is null || leader is null) throw new WorkspaceConflictException("Selected agents must have the BA and Leader roles in this company.");
                bindings = [ba, leader];
            }
            var memorySnapshot = CanonicalFrom(events);
            var spec = new SpecArtifact($"spec-{Guid.NewGuid():N}", objective, sourceRefs, "produced", memorySnapshot);
            var packet = HandoffPacket.Create(companyId, runId, spec, leaderAgentId, baAgentId);
            await AppendUnsafeAsync(companyId, runId,
            [
                DomainEvent.Create("workflow.created", new { objective, status = "awaiting_handoff", idempotencyKey, requestHash, leaderAgentId, baAgentId, bindings, memorySnapshot }),
                DomainEvent.Create("spec.produced", spec),
                DomainEvent.Create("handoff.requested", packet)
            ], events, cancellationToken);
            var createdRun = RebuildRun(companyId, runId, events.Where(item => item.RunId == runId).ToArray())
                ?? throw new InvalidDataException("Created workflow could not be rebuilt from its event records.");
            return new CreateRunResult(createdRun, true);
        }
        finally { _gate.Release(); }
    }

    public async Task AppendAsync(string companyId, string runId, IReadOnlyList<DomainEvent> events, CancellationToken cancellationToken)
    {
        Identifiers.Require(runId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = await ReadEventsUnsafeAsync(companyId, 0, cancellationToken);
            await AppendUnsafeAsync(companyId, runId, events, existing, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<WorkflowRun?> GetRunAsync(string companyId, string runId, CancellationToken cancellationToken)
    {
        var events = await ReadEventsAsync(companyId, 0, cancellationToken);
        return RebuildRun(companyId, runId, events.Where(item => item.RunId == runId).ToArray());
    }

    public async Task<IReadOnlyList<WorkflowRun>> ListRunsAsync(string companyId, CancellationToken cancellationToken)
    {
        var events = await ReadEventsAsync(companyId, 0, cancellationToken);
        return events.GroupBy(item => item.RunId)
            .Select(group => RebuildRun(companyId, group.Key, group.ToArray()))
            .Where(run => run is not null)
            .Cast<WorkflowRun>()
            .OrderByDescending(run => run.LastSequence)
            .ToArray();
    }

    public Task<IReadOnlyList<DomainEvent>> ReadEventsAsync(string companyId, long afterSequence, CancellationToken cancellationToken) =>
        ReadEventsLockedAsync(companyId, afterSequence, cancellationToken);

    public async Task<CommandResult> AcknowledgeHandoffAsync(
        string companyId, string runId, string packetHash, string receiverAgentId, string idempotencyKey, CancellationToken cancellationToken, long? expectedSequence = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var events = await ReadEventsUnsafeAsync(companyId, 0, cancellationToken);
            var runEvents = events.Where(item => item.RunId == runId).ToArray();
            var run = RebuildRun(companyId, runId, runEvents);
            if (run is null) return new(CommandOutcome.NotFound, null);
            var priorAck = runEvents.FirstOrDefault(item => item.Type == "handoff.acknowledged" && ReadString(item.Data, "idempotencyKey") == idempotencyKey);
            if (priorAck is not null)
            {
                if (ReadString(priorAck.Data, "packetHash") != packetHash || ReadString(priorAck.Data, "receiverAgentId") != receiverAgentId)
                    return new(CommandOutcome.Conflict, run, "Idempotency key was already used for a different acknowledgement.");
                if (run.State is "paused" or "cancelled") return new(CommandOutcome.AlreadyApplied, run);
                if (run.ExecutionMode == "local_model") return new(CommandOutcome.AlreadyApplied, run);
                var missingEvents = new List<DomainEvent>();
                if (!runEvents.Any(item => item.Type == "plan.produced"))
                {
                    var recoveredPlan = new DeliveryPlan($"plan-{Guid.NewGuid():N}", run.Handoff?.HandoffId ?? "",
                        ["Review requirements and source references", "Break work into bounded tasks", "Request Owner approval before implementation"], "produced");
                    missingEvents.Add(DomainEvent.Create("plan.produced", recoveredPlan));
                }
                if (!runEvents.Any(item => item.Type == "approval.required"))
                    missingEvents.Add(DomainEvent.Create("approval.required", new { approvalId = $"approval-{Guid.NewGuid():N}", action = "approve-plan" }));
                if (missingEvents.Count == 0) return new(CommandOutcome.AlreadyApplied, run);
                await AppendUnsafeAsync(companyId, runId, missingEvents, events, cancellationToken);
                var recoveredEvents = await ReadEventsUnsafeAsync(companyId, 0, cancellationToken);
                return new(CommandOutcome.Applied, RebuildRun(companyId, runId, recoveredEvents.Where(item => item.RunId == runId).ToArray()));
            }
            if (expectedSequence is not null && run.LastSequence != expectedSequence)
                return new(CommandOutcome.Conflict, run, "The run changed; reload its checkpoint.");
            if (!string.Equals(receiverAgentId, run.LeaderAgentId ?? "leader-agent", StringComparison.Ordinal))
                return new(CommandOutcome.Invalid, run, "This workflow is assigned to another Leader agent.");
            if (run.Handoff is null || !string.Equals(run.Handoff.ContentSha256, packetHash, StringComparison.Ordinal))
                return new(CommandOutcome.Invalid, run, "Handoff packet hash does not match the persisted packet.");
            if (run.Handoff.SpecSha256 is not null && (run.Handoff.CalculateHash() != packetHash || RuntimeJson.Hash(run.Spec) != run.Handoff.SpecSha256))
                return new(CommandOutcome.Invalid, run, "Handoff content or source artifact digest does not match.");
            if (run.State != "awaiting_handoff") return new(CommandOutcome.Conflict, run, "Run is not waiting for a handoff acknowledgement.");

            if (run.ExecutionMode == "local_model")
            {
                var job = LeaderJob(run, runEvents);
                await AppendUnsafeAsync(companyId, runId,
                    [DomainEvent.Create("handoff.acknowledged", new { packetHash, receiverAgentId, idempotencyKey }), DomainEvent.Create("invocation.queued", job)], events, cancellationToken);
                return new(CommandOutcome.Applied, RebuildRun(companyId, runId, events.Where(e => e.RunId == runId).ToArray()));
            }

            var plan = new DeliveryPlan($"plan-{Guid.NewGuid():N}", run.Handoff.HandoffId,
                ["Review requirements and source references", "Break work into bounded tasks", "Request Owner approval before implementation"], "produced");
            await AppendUnsafeAsync(companyId, runId,
            [
                DomainEvent.Create("handoff.acknowledged", new { packetHash, receiverAgentId, idempotencyKey }),
                DomainEvent.Create("plan.produced", plan),
                DomainEvent.Create("approval.required", new { approvalId = $"approval-{Guid.NewGuid():N}", action = "approve-plan" })
            ], events, cancellationToken);
            var updatedEvents = await ReadEventsUnsafeAsync(companyId, 0, cancellationToken);
            return new(CommandOutcome.Applied, RebuildRun(companyId, runId, updatedEvents.Where(item => item.RunId == runId).ToArray()));
        }
        finally { _gate.Release(); }
    }

    public async Task<CommandResult> DecideApprovalAsync(
        string companyId, string runId, bool approved, string idempotencyKey, CancellationToken cancellationToken, long? expectedSequence = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var events = await ReadEventsUnsafeAsync(companyId, 0, cancellationToken);
            var runEvents = events.Where(item => item.RunId == runId).ToArray();
            var run = RebuildRun(companyId, runId, runEvents);
            if (run is null) return new(CommandOutcome.NotFound, null);
            var previousDecision = runEvents.FirstOrDefault(item => item.Type == "owner.approval.decided" && ReadString(item.Data, "idempotencyKey") == idempotencyKey);
            if (previousDecision is not null)
            {
                var previousApproved = previousDecision.Data.TryGetProperty("approved", out var decision) && decision.GetBoolean();
                return previousApproved == approved
                    ? new(CommandOutcome.AlreadyApplied, run)
                    : new(CommandOutcome.Conflict, run, "Idempotency key was already used for a different approval decision.");
            }
            if (expectedSequence is not null && run.LastSequence != expectedSequence)
                return new(CommandOutcome.Conflict, run, "The run changed; reload its checkpoint.");
            if (run.State != "awaiting_approval") return new(CommandOutcome.Conflict, run, "Run has no pending Owner approval.");

            await AppendUnsafeAsync(companyId, runId,
            [DomainEvent.Create("owner.approval.decided", new { approved, idempotencyKey })], events, cancellationToken);
            var updatedEvents = await ReadEventsUnsafeAsync(companyId, 0, cancellationToken);
            return new(CommandOutcome.Applied, RebuildRun(companyId, runId, updatedEvents.Where(item => item.RunId == runId).ToArray()));
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<DomainEvent>> ReadEventsLockedAsync(string companyId, long afterSequence, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await ReadEventsUnsafeAsync(companyId, afterSequence, cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task<List<DomainEvent>> ReadEventsUnsafeAsync(string companyId, long afterSequence, CancellationToken cancellationToken)
    {
        var events = await _journal.ReadAsync(companyId, cancellationToken);
        return events.Where(item => item.Seq > afterSequence).ToList();
    }

    private async Task AppendUnsafeAsync(string companyId, string runId, IReadOnlyList<DomainEvent> additions,
        List<DomainEvent> existing, CancellationToken cancellationToken)
    {
        if (_maintenance) throw new WorkspaceConflictException("Ứng dụng đang chuẩn bị nâng cấp; vui lòng đợi khởi động lại.");
        var sealedEvents = EventIntegrity.Seal(companyId, runId, existing, additions);
        await _journal.AppendAsync(companyId, existing.Count, sealedEvents, cancellationToken);
        existing.AddRange(sealedEvents);
    }

    private static WorkflowRun? RebuildRun(string companyId, string runId, IReadOnlyList<DomainEvent> events)
    {
        if (!events.Any(item => item.Type == "workflow.created")) return null;
        string objective = "";
        string state = "unknown";
        var owner = "ba-agent";
        SpecArtifact? spec = null;
        HandoffPacket? handoff = null;
        string? acknowledgedBy = null;
        DeliveryPlan? plan = null;
        bool? ownerApproved = null;
        string? resumeState = null;
        string? leaderAgentId = null;
        var executionMode = "scaffold";
        string? lastError = null;
        foreach (var item in events.OrderBy(item => item.Seq))
        {
            switch (item.Type)
            {
                case "workflow.created":
                    objective = ReadString(item.Data, "objective") ?? objective;
                    executionMode = ReadString(item.Data, "mode") ?? "scaffold";
                    state = executionMode == "local_model" ? "queued" : "awaiting_handoff";
                    owner = ReadString(item.Data, "baAgentId") ?? "ba-agent";
                    leaderAgentId = ReadString(item.Data, "leaderAgentId") ?? "leader-agent";
                    break;
                case "spec.produced":
                    spec = item.Data.Deserialize<SpecArtifact>(JsonOptions);
                    break;
                case "handoff.requested":
                    state = "awaiting_handoff";
                    handoff = item.Data.Deserialize<HandoffPacket>(JsonOptions);
                    break;
                case "handoff.acknowledged":
                    acknowledgedBy = ReadString(item.Data, "receiverAgentId");
                    owner = acknowledgedBy ?? "leader-agent";
                    break;
                case "plan.produced":
                    plan = item.Data.Deserialize<DeliveryPlan>(JsonOptions);
                    state = "awaiting_approval";
                    break;
                case "invocation.queued":
                case "invocation.retry_requested":
                    state = "queued";
                    owner = ReadString(item.Data, "agentId") ?? owner;
                    lastError = null;
                    break;
                case "invocation.started":
                    state = "running";
                    owner = ReadString(item.Data, "agentId") ?? owner;
                    break;
                case "invocation.failed":
                case "invocation.interrupted":
                    lastError = ReadString(item.Data, "errorCode");
                    if (state is not ("paused" or "cancelled")) state = "blocked";
                    break;
                case "workflow.paused":
                    resumeState = ReadString(item.Data, "resumeState");
                    state = "paused";
                    break;
                case "workflow.resumed":
                    state = resumeState ?? "awaiting_handoff";
                    resumeState = null;
                    break;
                case "workflow.cancelled":
                    state = "cancelled";
                    resumeState = null;
                    break;
                case "owner.approval.decided":
                    ownerApproved = item.Data.TryGetProperty("approved", out var decision) && decision.GetBoolean();
                    state = ownerApproved.Value ? "approved" : "revision_requested";
                    break;
                case "delivery.prepared":
                    state = "awaiting_delivery_input";
                    owner = item.Data.GetProperty("bindings").Deserialize<DeliveryBinding[]>(JsonOptions)!.Single(b => b.RoleId == "dev").Agent.AgentId;
                    break;
                case "delivery.artifact.produced":
                    state = "checking_delivery";
                    lastError = null;
                    break;
                case "delivery.report.produced":
                    state = ReadString(item.Data, "status") == "ready_for_owner" ? "awaiting_delivery_approval" : "delivery_blocked";
                    owner = "owner";
                    break;
                case "delivery.reviewed":
                    state = item.Data.GetProperty("approved").GetBoolean() ? "delivered" : "delivery_revision_requested";
                    break;
            }
        }
        return new WorkflowRun(companyId, runId, objective, state, owner, spec, handoff, acknowledgedBy, plan,
            ownerApproved, events.Max(item => item.Seq), LastError: lastError, ResumeState: resumeState, LeaderAgentId: leaderAgentId,
            ExecutionMode: executionMode, Invocations: JobsFrom(events), Delivery: DeliveryFrom(events));
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
