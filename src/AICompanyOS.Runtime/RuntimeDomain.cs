using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AICompanyOS.Runtime;

public sealed record CreateRunRequest(string Objective, string[]? SourceRefs);
public sealed record HandoffAckRequest(string PacketHash, string ReceiverAgentId);
public sealed record OwnerApprovalRequest(bool Approved);

public sealed record SpecArtifact(string ArtifactId, string Objective, string[] SourceRefs, string VerificationState);

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
    string ContentSha256)
{
    public static HandoffPacket Create(string companyId, string runId, SpecArtifact spec)
    {
        var snapshotId = $"ctx-{Guid.NewGuid():N}";
        var handoffId = $"handoff-{Guid.NewGuid():N}";
        var content = JsonSerializer.Serialize(new
        {
            schema = "ai-company-os/handoff/v1",
            companyId,
            runId,
            fromAgentId = "ba-agent",
            toRoleId = "leader",
            specArtifactId = spec.ArtifactId,
            sourceRefs = spec.SourceRefs,
            contextSnapshotId = snapshotId,
            nextAction = "Review the requirement spec and produce a delivery plan."
        });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        return new HandoffPacket(handoffId, companyId, runId, "ba-agent", "leader", spec.ArtifactId,
            spec.SourceRefs, snapshotId, "Review the requirement spec and produce a delivery plan.", $"sha256:{hash}");
    }
}

public sealed record DeliveryPlan(string ArtifactId, string HandoffId, string[] Steps, string VerificationState);

public sealed record DomainEvent(long Seq, string EventId, string RunId, string Type, DateTimeOffset CreatedAt, JsonElement Data)
{
    public static DomainEvent Create(string type, object data) =>
        new(0, $"evt-{Guid.NewGuid():N}", "", type, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(data));
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
    string? LastError = null);

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
    string[] Limitations)
{
    public static WorkflowReport FromRun(WorkflowRun run) => new(
        run.RunId,
        run.State,
        run.Objective,
        new[] { run.Spec?.ArtifactId, run.Plan?.ArtifactId }.Where(value => value is not null).Cast<string>().ToArray(),
        run.Spec?.SourceRefs ?? [],
        run.OwnerApproved is null && run.Plan is not null ? ["owner-plan-approval"] : [],
        ["BA and Leader outputs are deterministic local scaffolding, not AI inference.", "No code tools, providers, tests, or release actions were executed."]);
}

public sealed class RuntimeSession(string token)
{
    public string Token { get; } = token;
    public bool IsValid(string candidate) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Token), Encoding.UTF8.GetBytes(candidate));
}