using System.Security.Cryptography;
using System.Text.Json;
using AICompanyOS.Runtime;

var dataRoot = Path.Combine(Path.GetTempPath(), $"aicos-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(dataRoot);
try
{
    const string companyId = "test-company";
    const string runId = "run-test-001";
    var initialStore = new FileEventStore(dataRoot);
    var created = await initialStore.CreateRunAsync(companyId, runId, "Build a local workflow", ["source-1"], "create-run-1", CancellationToken.None);
    Assert(created.Created, "The first request must create a workflow run.");
    var duplicateCreate = await initialStore.CreateRunAsync(companyId, "run-test-duplicate", "Build a local workflow", ["source-1"], "create-run-1", CancellationToken.None);
    Assert(!duplicateCreate.Created && duplicateCreate.Run.RunId == runId, "A retried create command must return its original run.");
    var changedCreate = await initialStore.CreateRunAsync(companyId, "run-test-changed", "Different objective", ["source-1"], "create-run-1", CancellationToken.None);
    Assert(changedCreate.Conflict, "Reusing a create key with a different payload must be rejected.");
    var spec = created.Run.Spec ?? throw new InvalidOperationException("The initial run did not produce its spec.");
    var packet = created.Run.Handoff ?? throw new InvalidOperationException("The initial run did not create its handoff.");

    var competingWriterRejected = false;
    try { using var competingStore = new FileEventStore(dataRoot); }
    catch (IOException) { competingWriterRejected = true; }
    Assert(competingWriterRejected, "A second runtime must not open the same File Store for writing.");
    var cursorEvents = await initialStore.ReadEventsAsync(companyId, 2, CancellationToken.None);
    Assert(cursorEvents.Count == 1 && cursorEvents[0].Seq == 3, "Event replay must honor the requested sequence cursor.");

    var partialObjective = "Recover partial create";
    var partialSources = Array.Empty<string>();
    var partialHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        companyId,
        objective = partialObjective,
        sourceRefs = partialSources
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web)))).ToLowerInvariant();
    await initialStore.AppendAsync(companyId, "run-partial-create",
        [DomainEvent.Create("workflow.created", new { objective = partialObjective, status = "awaiting_handoff", idempotencyKey = "partial-create-1", requestHash = partialHash })],
        CancellationToken.None);
    var recoveredCreate = await initialStore.CreateRunAsync(companyId, "unused-run-id", partialObjective, partialSources, "partial-create-1", CancellationToken.None);
    Assert(!recoveredCreate.Created && recoveredCreate.Run.Spec is not null && recoveredCreate.Run.Handoff is not null,
        "Retrying a partially persisted create command must finish its missing artifact and handoff events.");

    var partialAckRun = await initialStore.CreateRunAsync(companyId, "run-partial-ack", "Recover partial ACK", [], "partial-ack-create", CancellationToken.None);
    var partialAckPacket = partialAckRun.Run.Handoff ?? throw new InvalidOperationException("Partial ACK fixture has no handoff packet.");
    await initialStore.AppendAsync(companyId, partialAckRun.Run.RunId,
        [DomainEvent.Create("handoff.acknowledged", new { packetHash = partialAckPacket.ContentSha256, receiverAgentId = "leader-agent", idempotencyKey = "partial-ack-1" })],
        CancellationToken.None);
    var recoveredAck = await initialStore.AcknowledgeHandoffAsync(companyId, partialAckRun.Run.RunId,
        partialAckPacket.ContentSha256, "leader-agent", "partial-ack-1", CancellationToken.None);
    Assert(recoveredAck.Outcome == CommandOutcome.Applied && recoveredAck.Run?.State == "awaiting_approval",
        "Retrying a partially persisted ACK must finish plan and approval events.");

    var badHash = await initialStore.AcknowledgeHandoffAsync(companyId, runId, "sha256:wrong", "leader-agent", "ack-wrong", CancellationToken.None);
    Assert(badHash.Outcome == CommandOutcome.Invalid, "A mismatched handoff hash must be rejected.");
    var wrongReceiver = await initialStore.AcknowledgeHandoffAsync(companyId, runId, packet.ContentSha256, "other-agent", "ack-wrong-agent", CancellationToken.None);
    Assert(wrongReceiver.Outcome == CommandOutcome.Invalid, "Only the assigned Leader may acknowledge this packet.");

    var accepted = await initialStore.AcknowledgeHandoffAsync(companyId, runId, packet.ContentSha256, "leader-agent", "ack-1", CancellationToken.None);
    Assert(accepted.Outcome == CommandOutcome.Applied, "A valid handoff must be accepted.");
    var acceptedRun = accepted.Run ?? throw new InvalidOperationException("Accepted handoff did not return its run.");
    Assert(acceptedRun.State == "awaiting_approval", "Handoff acceptance must produce a plan and require Owner approval.");
    Assert(acceptedRun.CurrentOwner == "leader-agent", "Ownership must move to the acknowledging receiver.");

    var repeatedAck = await initialStore.AcknowledgeHandoffAsync(companyId, runId, packet.ContentSha256, "leader-agent", "ack-1", CancellationToken.None);
    Assert(repeatedAck.Outcome == CommandOutcome.AlreadyApplied, "An idempotent retry must not apply twice.");
    Assert(repeatedAck.Run?.LastSequence == acceptedRun.LastSequence, "An idempotent retry must not append duplicate events.");

    var logPath = Path.Combine(dataRoot, "companies", companyId, "events.jsonl");
    await File.AppendAllTextAsync(logPath, "{\"partial\":");
    initialStore.Dispose();

    var restartedStore = new FileEventStore(dataRoot);
    var restored = await restartedStore.GetRunAsync(companyId, runId, CancellationToken.None);
    Assert(restored?.State == "awaiting_approval", "A new store instance must recover the workflow from the event log.");
    Assert(restored?.Handoff?.ContentSha256 == packet.ContentSha256, "The persisted handoff hash must survive restart.");
    Assert(restored?.Plan?.ArtifactId == acceptedRun.Plan?.ArtifactId, "The produced plan must survive restart.");
    Assert((await File.ReadAllTextAsync(logPath)).EndsWith('\n'), "A torn final event must be truncated before replay or future appends.");
    Assert(await restartedStore.GetRunAsync("other-company", runId, CancellationToken.None) is null, "Company logs must not leak into another company scope.");

    var approved = await restartedStore.DecideApprovalAsync(companyId, runId, true, "owner-approval-1", CancellationToken.None);
    Assert(approved.Outcome == CommandOutcome.Applied && approved.Run?.State == "approved", "Owner approval must be an explicit durable event.");
    var repeatedApproval = await restartedStore.DecideApprovalAsync(companyId, runId, true, "owner-approval-1", CancellationToken.None);
    Assert(repeatedApproval.Outcome == CommandOutcome.AlreadyApplied, "Approval retries must be idempotent.");
    var changedApproval = await restartedStore.DecideApprovalAsync(companyId, runId, false, "owner-approval-1", CancellationToken.None);
    Assert(changedApproval.Outcome == CommandOutcome.Conflict, "Reusing an approval key with a different decision must be rejected.");
    restartedStore.Dispose();

    Console.WriteLine("PASS: create/ACK/approval idempotent; invalid hash/receiver rejected; state recovers after restart; torn tail repaired; cursor and company scope verified.");
}
finally
{
    Directory.Delete(dataRoot, recursive: true);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}