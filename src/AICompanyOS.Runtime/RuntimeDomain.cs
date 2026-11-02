using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AICompanyOS.Runtime;

public sealed record CreateRunRequest(string Objective, string[]? SourceRefs, string? LeaderAgentId = null, string? BaAgentId = null, string ExecutionMode = "scaffold", string? ModelId = null);
public sealed record HandoffAckRequest(string PacketHash, string ReceiverAgentId, long? ExpectedSequence = null);
public sealed record OwnerApprovalRequest(bool Approved, long? ExpectedSequence = null);
public sealed record RunControlRequest(long ExpectedSequence);

public sealed record SpecArtifact(string ArtifactId, string Objective, string[] SourceRefs, string VerificationState, MemoryRecord[]? CanonicalRecords = null);

public sealed record HandoffPacket(
    string HandoffId,
    string CompanyId,
    string RunId,
    string FromAgentId,
    string ToRoleId,
    string SpecArtifactId,
    string[] SourceRefs,
    string ContextSnapshotId,
    string NextAction,
    string ContentSha256,
    string? SpecSha256 = null,
    string? ToAgentId = null)
{
    public static HandoffPacket Create(string companyId, string runId, SpecArtifact spec,
        string leaderAgentId = "leader-agent", string baAgentId = "ba-agent")
    {
        var packet = new HandoffPacket($"handoff-{Guid.NewGuid():N}", companyId, runId, baAgentId, "leader", spec.ArtifactId,
            spec.SourceRefs, $"ctx-{Guid.NewGuid():N}", "Review the requirement spec and produce a delivery plan.", "",
            RuntimeJson.Hash(spec), leaderAgentId);
        return packet with { ContentSha256 = packet.CalculateHash() };
    }

    public string CalculateHash() => RuntimeJson.Hash(new
    {
        schema = "ai-company-os/handoff/v1", HandoffId, CompanyId, RunId, FromAgentId, ToRoleId,
        SpecArtifactId, SourceRefs, ContextSnapshotId, NextAction, SpecSha256, ToAgentId
    });
}

public sealed record DeliveryPlan(string ArtifactId, string HandoffId, string[] Steps, string VerificationState);

public sealed record DomainEvent(long Seq, string EventId, string RunId, string Type, DateTimeOffset CreatedAt, JsonElement Data,
    string? CompanyId = null, string? PrevHash = null, string? ContentSha256 = null)
{
    public static DomainEvent Create(string type, object data) =>
        new(0, $"evt-{Guid.NewGuid():N}", "", type, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(data, RuntimeJson.Options));
}

public sealed record WorkflowRun(
    string CompanyId,
    string RunId,
    string Objective,
    string State,
    string CurrentOwner,
    SpecArtifact? Spec,
    HandoffPacket? Handoff,
    string? AcknowledgedBy,
    DeliveryPlan? Plan,
    bool? OwnerApproved,
    long LastSequence,
    string? LastError = null,
    string? ResumeState = null,
    string? LeaderAgentId = null,
    string ExecutionMode = "scaffold",
    InferenceJob[]? Invocations = null,
    DeliverySession? Delivery = null);

public sealed record CreateRunResult(WorkflowRun Run, bool Created, bool Conflict = false);
public sealed record CommandResult(CommandOutcome Outcome, WorkflowRun? Run, string? Message = null);
public enum CommandOutcome { Applied, AlreadyApplied, NotFound, Conflict, Invalid }

public sealed record WorkflowReport(
    string RunId,
    string State,
    string Objective,
    string[] ProducedArtifacts,
    string[] EvidenceRefs,
    string[] OutstandingApprovals,
    string[] Limitations,
    string[]? SourceRefs = null,
    long CheckpointSequence = 0,
    InferenceJob[]? ModelInvocations = null,
    DeliverySession? Delivery = null)
{
    public static WorkflowReport FromRun(WorkflowRun run) => new(
        run.RunId,
        run.State,
        run.Objective,
        new[] { run.Spec?.ArtifactId, run.Plan?.ArtifactId, run.Delivery?.Artifact?.ArtifactId }.Where(value => value is not null).Cast<string>().ToArray(),
        run.Delivery?.Receipts?.Select(r => r.ReceiptId).ToArray() ?? [],
        run.State == "awaiting_delivery_approval" || run.State == "paused" && run.ResumeState == "awaiting_delivery_approval" ? ["owner-delivery-approval"] : run.State == "awaiting_approval" || run.State == "paused" && run.ResumeState == "awaiting_approval" ? ["owner-plan-approval"] : [],
        run.Delivery is not null ? ["Static-document checks are in-process validators, not software builds, penetration tests or legal clearance.", "No repository, shell, publish or Git action was executed."] : run.ExecutionMode == "local_model"
            ? ["Model outputs are unverified proposals; token counts are provider-reported when available.", "No code tools, software tests or release actions were executed. Owner plan approval grants no execution permission."]
            : ["BA and Leader outputs are deterministic local scaffolding, not AI inference.", "No code tools, providers, tests, or release actions were executed. Owner plan approval grants no execution permission."],
        run.Spec?.SourceRefs ?? [], run.LastSequence, run.Invocations, run.Delivery);
}

public sealed class RuntimeSession(string token)
{
    public string Token { get; } = token;
    public bool IsValid(string candidate) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Token), Encoding.UTF8.GetBytes(candidate));
}
