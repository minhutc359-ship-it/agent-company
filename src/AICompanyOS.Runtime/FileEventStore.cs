using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace AICompanyOS.Runtime;

public sealed class FileEventStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FileStream _writerLock;

    public FileEventStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        _writerLock = new FileStream(Path.Combine(_root, "runtime.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    public void Dispose()
    {
        _writerLock.Dispose();
        _gate.Dispose();
    }

    public async Task<CreateRunResult> CreateRunAsync(
        string companyId, string runId, string objective, string[] sourceRefs, string idempotencyKey, CancellationToken cancellationToken)
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
                var recoveredSpec = existingSpec ?? new SpecArtifact($"spec-{Guid.NewGuid():N}", objective, sourceRefs, "produced");
                if (existingSpec is null) additions.Add(DomainEvent.Create("spec.produced", recoveredSpec));
                if (!existingRunEvents.Any(item => item.Type == "handoff.requested"))
                    additions.Add(DomainEvent.Create("handoff.requested", HandoffPacket.Create(companyId, prior.RunId, recoveredSpec)));
                if (additions.Count > 0)
                    await AppendUnsafeAsync(companyId, prior.RunId, additions, events, cancellationToken);
                if (additions.Count > 0)
                    existingRun = RebuildRun(companyId, prior.RunId, events.Where(item => item.RunId == prior.RunId).ToArray())
                        ?? throw new InvalidDataException("Recovered workflow could not be rebuilt from its event records.");
                return new CreateRunResult(existingRun, false);
            }

            var spec = new SpecArtifact($"spec-{Guid.NewGuid():N}", objective, sourceRefs, "produced");
            var packet = HandoffPacket.Create(companyId, runId, spec);
            await AppendUnsafeAsync(companyId, runId,
            [
                DomainEvent.Create("workflow.created", new { objective, status = "awaiting_handoff", idempotencyKey, requestHash }),
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
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = await ReadEventsUnsafeAsync(companyId, 0, cancellationToken);
            var path = GetLogPath(companyId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            foreach (var item in events)
            {
                var persisted = item with { Seq = existing.Count + 1, RunId = runId };
                var bytes = JsonSerializer.SerializeToUtf8Bytes(persisted, JsonOptions);
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
                existing.Add(persisted);
            }
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            _gate.Release();
        }
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
        string companyId, string runId, string packetHash, string receiverAgentId, string idempotencyKey, CancellationToken cancellationToken)
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
            if (!string.Equals(receiverAgentId, "leader-agent", StringComparison.Ordinal))
                return new(CommandOutcome.Invalid, run, "This workflow slice is assigned to leader-agent.");
            if (run.Handoff is null || !string.Equals(run.Handoff.ContentSha256, packetHash, StringComparison.Ordinal))
                return new(CommandOutcome.Invalid, run, "Handoff packet hash does not match the persisted packet.");
            if (run.State != "awaiting_handoff") return new(CommandOutcome.Conflict, run, "Run is not waiting for a handoff acknowledgement.");

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
        string companyId, string runId, bool approved, string idempotencyKey, CancellationToken cancellationToken)
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
        var path = GetLogPath(companyId);
        if (!File.Exists(path)) return [];
        var result = new List<DomainEvent>();
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var lastNewline = Array.LastIndexOf(bytes, (byte)'\n');
        var validLength = lastNewline + 1;
        if (validLength != bytes.Length)
        {
            await using var repair = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            repair.SetLength(validLength);
            repair.Flush(flushToDisk: true);
        }
        var text = Encoding.UTF8.GetString(bytes, 0, validLength);
        var expectedSequence = 1L;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var item = JsonSerializer.Deserialize<DomainEvent>(line.TrimEnd('\r'), JsonOptions)
                ?? throw new InvalidDataException("The event log contains an empty event record.");
            if (item.Seq != expectedSequence)
                throw new InvalidDataException("The event log sequence is not contiguous.");
            expectedSequence++;
            if (item.Seq > afterSequence) result.Add(item);
        }
        return result;
    }

    private async Task AppendUnsafeAsync(string companyId, string runId, IReadOnlyList<DomainEvent> additions,
        List<DomainEvent> existing, CancellationToken cancellationToken)
    {
        var path = GetLogPath(companyId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        foreach (var item in additions)
        {
            var persisted = item with { Seq = existing.Count + 1, RunId = runId };
            await JsonSerializer.SerializeAsync(stream, persisted, JsonOptions, cancellationToken);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
            existing.Add(persisted);
        }
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private string GetLogPath(string companyId)
    {
        if (companyId.Length is 0 or > 100 ||
            companyId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("Company ID contains invalid path characters.", nameof(companyId));
        return Path.Combine(_root, "companies", companyId, "events.jsonl");
    }

    private static WorkflowRun? RebuildRun(string companyId, string runId, IReadOnlyList<DomainEvent> events)
    {
        if (events.Count == 0) return null;
        string objective = "";
        string state = "unknown";
        var owner = "ba-agent";
        SpecArtifact? spec = null;
        HandoffPacket? handoff = null;
        string? acknowledgedBy = null;
        DeliveryPlan? plan = null;
        bool? ownerApproved = null;
        foreach (var item in events.OrderBy(item => item.Seq))
        {
            switch (item.Type)
            {
                case "workflow.created":
                    objective = ReadString(item.Data, "objective") ?? objective;
                    state = "awaiting_handoff";
                    break;
                case "spec.produced":
                    spec = item.Data.Deserialize<SpecArtifact>(JsonOptions);
                    break;
                case "handoff.requested":
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
                case "owner.approval.decided":
                    ownerApproved = item.Data.TryGetProperty("approved", out var decision) && decision.GetBoolean();
                    state = ownerApproved.Value ? "approved" : "revision_requested";
                    break;
            }
        }
        return new WorkflowRun(companyId, runId, objective, state, owner, spec, handoff, acknowledgedBy, plan,
            ownerApproved, events.Max(item => item.Seq));
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}