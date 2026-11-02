namespace AICompanyOS.Runtime;

public sealed record SoftwareHandoff(string HandoffId, string JobId, string CompanyId, string RunId, int Round,
    string FromAgentId, string FromRoleId, string ToAgentId, string ToRoleId, string InputSha256,
    string? SpecSha256, string? PlanSha256, string? CandidateSha256, string[] AcceptedMemoryRefs,
    Citation[] SourceRefs, string[] ToolPermissions, string ContentSha256);
public sealed record SoftwareHandoffAck(string JobId, string HandoffId, string ContentSha256, string ReceiverAgentId,
    string ReceiverRoleId, string InputSha256, string LeaseToken, string Mode = "runtime_context_receiver");

public sealed partial class FileEventStore
{
    private static SoftwareHandoff BuildSoftwareHandoff(SoftwareRun run, SoftwareJob job)
    {
        var fromRole = job.NodeId switch { "ba" => "owner", "leader" => "ba", "build" => "dev", "tester" or "soc" or "qa" => "dev", _ => "leader" };
        var from = fromRole == "owner" ? "owner" : run.Seed.Bindings.First(b => b.RoleId == fromRole).Agent.AgentId;
        var permissions = job.TaskId is not null && run.Grant is not null && Node(run, "approval-plan").Status == "approved"
            ? new[] { "repo.read.pinned", "worktree.patch.scoped" }
            : job.NodeId is "build" or "tester" && run.Grant is not null && Node(run, "approval-plan").Status == "approved"
                ? new[] { "container.command.pinned.network_none" } : new[] { "context.read.pinned" };
        var packet = new SoftwareHandoff("handoff-" + job.JobId + "-" + job.InputSha256[7..19], job.JobId, job.CompanyId, job.RunId, job.Round,
            from, fromRole, job.AgentId, job.RoleId, job.InputSha256, run.Spec is null ? null : RuntimeJson.Hash(run.Spec),
            run.Plan is null ? null : RuntimeJson.Hash(run.Plan), run.Candidate?.Sha256,
            run.Seed.Memory.Select(m => m.StableKey + "@" + m.Version).ToArray(), run.Seed.Sources.Select(s => s.Citation).ToArray(), permissions, "");
        return packet with { ContentSha256 = RuntimeJson.Hash(packet) };
    }
    private static DomainEvent AcknowledgeSoftwareContext(SoftwareRun run, SoftwareJob job)
    {
        var packet = run.Handoffs?.LastOrDefault(p => p.JobId == job.JobId && p.InputSha256 == job.InputSha256);
        var expected = BuildSoftwareHandoff(run, job);
        if (packet is null || packet.ContentSha256 != expected.ContentSha256 || packet.ToAgentId != job.AgentId || packet.ToRoleId != job.RoleId || RuntimeJson.TextHash(job.Input) != packet.InputSha256 || job.LeaseToken is null)
            throw new WorkspaceConflictException("Handoff identity, artifact version or context hash changed; no invocation was acknowledged.");
        return DomainEvent.Create("software.handoff.acknowledged", new SoftwareHandoffAck(job.JobId, packet.HandoffId, packet.ContentSha256, job.AgentId, job.RoleId, job.InputSha256, job.LeaseToken));
    }
}
