using System.Text.Json;
using System.Text.RegularExpressions;

namespace AICompanyOS.Runtime;

public sealed record CompanyProfile(int Version, string Description, string[] Departments, bool AllowGitHubRelease = false,
    string PrivacyMode = "strict_local", int CloudPolicyVersion = 1, int MaxCloudOutputTokens = 2048);
public sealed record CompanyProfileRequest(int ExpectedVersion, string Description, string[] Departments, bool AllowGitHubRelease = false,
    string PrivacyMode = "strict_local", int CloudPolicyVersion = 1, int MaxCloudOutputTokens = 2048);
public sealed record AgentConfiguration(string AgentId, int Version, string Department, string? ModelId, string[] SkillRefs, int MaxInputBytes = 8000, int MaxOutputTokens = 4096);
public sealed record AgentConfigurationRequest(int ExpectedVersion, string Department, string? ModelId, string[] SkillRefs, int MaxInputBytes = 8000, int MaxOutputTokens = 4096);
public sealed record SkillPackage(string SkillId, string Version, string Name, string Instructions, string[] Roles, string[] RequestedTools);
public sealed record CommandSpec(string Name, string Executable, string[] Arguments, int TimeoutSeconds = 120);
public sealed record RepositoryProject(string ProjectId, int Version, string Name, string RepositoryPath, string[] ReadPaths, string[] WritePaths,
    string DockerImage, CommandSpec Build, CommandSpec Test, string? RemoteUrl = null, string BaseBranch = "master");
public sealed record ProjectRequest(string ProjectId, string Name, string RepositoryPath, string[] ReadPaths, string[] WritePaths,
    string DockerImage, CommandSpec Build, CommandSpec Test, int ExpectedVersion = 0, string? RemoteUrl = null, string BaseBranch = "master");
public sealed record RepositoryFile(string Path, string Content, string Sha256, string Mode = "100644");
public sealed record RepositorySnapshot(string BaseCommit, RepositoryFile[] Files, string[] OmittedPaths, string Sha256);
public sealed record WorkflowNode(string Id, string Kind, string? Role, string[] DependsOn, string When = "always", bool RunOnFailure = false, string? OnFail = null, int MaxLoop = 0);
public sealed record SoftwareWorkflow(string Name, string Version, WorkflowNode[] Nodes, int MaxParallel = 2, int MaxAttempts = 3, int MaxElapsedMinutes = 30, int MaxOutputTokens = 40000);
public sealed record WorkflowValidation(bool Valid, string[] Errors, string[] TopologicalOrder, string[] ExpandedNodeIds);
public sealed record RequirementCriterion(string Id, string Description, string Verification);
public sealed record RequirementSpec(string Title, string[] Goals, RequirementCriterion[] Acceptance, string[] Assumptions, string[] OpenQuestions);
public sealed record PlannedTask(string Id, string Title, string AgentId, string[] DependsOn, string[] AllowedPaths, string[] AcceptanceIds);
public sealed record TaskGraph(string Summary, PlannedTask[] Tasks);
public sealed record PatchFile(string Path, string? BeforeSha256, string Content);
public sealed record RepositoryPatch(string Summary, PatchFile[] Files, string[]? ReadRequests = null);
public sealed record TaskPatch(string TaskId, string JobId, int Round, RepositoryPatch Patch, string Sha256, string Worktree, DateTimeOffset ProducedAt, ExecutionReceipt? Receipt = null);
public sealed record IntegratedCandidate(string Sha256, int Round, RepositoryFile[] Files, string[] ChangedPaths, TaskPatch[] Patches);
public sealed record RuntimeFinding(string Id, string Severity, string Message, string? Path = null, string? CriterionId = null);
public sealed record ExecutionReceipt(string ReceiptId, string JobId, string RoleId, string AgentId, string Tool, string ToolVersion, string InputSha256,
    string Status, int? ExitCode, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, string Stdout, string Stderr, string LogSha256,
    string? ContainerId, string? ImageId, RuntimeFinding[] Findings, bool SelfReviewed = false, CommandSpec? Command = null, string? ArchiveSha256 = null, int Attempt = 0);
public sealed record RoleBinding(string RoleId, Agent Agent, AgentConfiguration Configuration, SkillPackage[] Skills, LocalModel? Model);
public sealed record ExecutionGrant(string ManifestSha256, string ImageId, string SnapshotSha256, string PlanSha256, DateTimeOffset PreviewedAt, DateTimeOffset ExpiresAt);
public sealed record SoftwareApproval(long ExpectedSequence, bool Approved, string ManifestSha256);
public sealed record SoftwareCreateRequest(string ProjectId, string Objective, Dictionary<string, string>? Assignments = null, string[]? SourceIds = null, string WorkflowName = "software-delivery", string WorkflowVersion = "2.0.0", bool ReleaseRequested = false);
public sealed record SoftwareControlRequest(long ExpectedSequence);
public sealed record SoftwareRevisionRequest(long ExpectedSequence, string Stage, string Feedback);
public sealed record OwnerFeedback(string Stage, string Feedback, int Round, DateTimeOffset CreatedAt);
public sealed record SoftwareOutputRequest(long ExpectedSequence, JsonElement Output);
public sealed record SoftwareReleaseRequest(long ExpectedSequence, string CandidateSha256, bool AcceptanceConfirmed, bool RightsConfirmed, bool PushRequested = false, string? GitHubToken = null, string? TargetManifestSha256 = null);
public sealed record ReleaseOutcome(string CommitSha, string Branch, bool Pushed, string? PullRequestUrl, string ManifestSha256);
public sealed record SoftwareJob(string JobId, string RunId, string CompanyId, string NodeId, string RoleId, string AgentId, int Round, string? TaskId,
    string Input, string InputSha256, string Status = "queued", int Attempts = 0, string? LeaseToken = null, string? WorkerId = null,
    DateTimeOffset? LeaseUntil = null, string? Error = null, int? InputTokens = null, int? OutputTokens = null, string? OutputSha256 = null, int ModelCalls = 0, int UnreportedCalls = 0);
public sealed record SoftwareNodeState(string Id, string Status, string? Error = null);
public sealed record SoftwareSeed(string RunId, string CompanyId, string Objective, RepositoryProject Project, RepositorySnapshot Repository,
    SoftwareWorkflow Workflow, RoleBinding[] Bindings, MemoryRecord[] Memory, SourceExcerpt[] Sources, bool ReleaseRequested, DateTimeOffset CreatedAt);
public sealed record SoftwareReport(string Status, string[] ArtifactRefs, string[] ReceiptIds, RuntimeFinding[] Findings, string[] OutstandingApprovals,
    int? ReportedInputTokens, int? ReportedOutputTokens, int ModelAttempts, string[] Limitations);
public sealed record SoftwareRun(SoftwareSeed Seed, string State, int Round, RequirementSpec? Spec, TaskGraph? Plan, SoftwareNodeState[] Nodes,
    SoftwareJob[] Jobs, TaskPatch[] Patches, IntegratedCandidate? Candidate, ExecutionReceipt[] Receipts, ExecutionGrant? Grant, SoftwareReport Report,
    long LastSequence, string? ResumeState = null, string? Error = null, ReleaseOutcome? Release = null, OwnerFeedback[]? OwnerFeedback = null, bool StepMode = false, SoftwareHandoff[]? Handoffs = null, SoftwareHandoffAck[]? HandoffAcks = null);
public sealed record PublicInteraction(string InteractionId, string RunId, string Type, string FromAgentId, string[] ToAgentIds, string Summary,
    string[] ArtifactRefs, Citation[] Citations, string? ReplyTo, string Status, string DedupeKey, DateTimeOffset CreatedAt, string Origin = "owner_authored", string BranchId = "main", string[]? ActionItems = null);
public sealed record InteractionRequest(string RunId, string Type, string FromAgentId, string[] ToAgentIds, string Summary, string[] ArtifactRefs, Citation[] Citations, string? ReplyTo = null, string BranchId = "main");
public sealed record ConversationBranch(string BranchId, string RunId, string Name, string? ParentBranchId, string? ForkMessageId, DateTimeOffset CreatedAt, int? ParentHistoryLength = null);
public sealed record ConversationBranchRequest(string BranchId, string RunId, string Name, string ParentBranchId = "main", string? ForkMessageId = null);
public sealed record Meeting(string MeetingId, string RunId, string Agenda, string[] Participants, string State, string? Summary, string[] ActionItems, Citation[] Citations, int Version);
public sealed record MeetingRequest(string RunId, string Agenda, string[] Participants);
public sealed record MeetingCompleteRequest(int ExpectedVersion, string Summary, string[] ActionItems, Citation[] Citations);

public static partial class SoftwareContracts
{
    public static readonly string[] Roles = ["ba", "leader", "dev", "tester", "soc", "qa", "subpm"];
    public static SoftwareWorkflow DefaultWorkflow => new("software-delivery", "2.0.0",
    [
        new("intake", "intake", null, []), new("ba", "agent", "ba", ["intake"]), new("leader", "agent", "leader", ["ba"]),
        new("approval-plan", "approval", null, ["leader"]), new("dev", "fanout", "dev", ["approval-plan"]),
        new("build", "build", "dev", ["dev"]), new("tester", "test", "tester", ["build"]),
        new("soc", "security", "soc", ["build"]), new("qa", "quality", "qa", ["build"]),
        new("gate", "gate", null, ["tester", "soc", "qa"], OnFail: "dev", MaxLoop: 2),
        new("subpm", "report", "subpm", ["gate"], RunOnFailure: true),
        new("final-approval", "approval", null, ["subpm"]), new("release", "release", null, ["final-approval"], "release_requested")
    ]);
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9._/-]{0,239}$", RegexOptions.CultureInvariant)]
    private static partial Regex RelativePattern();
    public static bool SafeRelative(string? path) => path is { Length: > 0 } && RelativePattern().IsMatch(path) && !path.EndsWith('/') &&
        path.Split('/').All(p => p is not ("." or "..") && !p.EndsWith('.') && !p.StartsWith('.') && !new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(p.Split('.')[0], StringComparer.OrdinalIgnoreCase));
    public static bool SecretPath(string path) => path.Split('/').Any(p => p.Equals("secrets", StringComparison.OrdinalIgnoreCase) || p.Equals("credentials", StringComparison.OrdinalIgnoreCase) ||
        p.Equals("node_modules", StringComparison.OrdinalIgnoreCase) || p.Equals("bin", StringComparison.OrdinalIgnoreCase) || p.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
        p.StartsWith(".env", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase));
    public static bool Matches(string path, string[] scopes) => SafeRelative(path) && !SecretPath(path) && scopes.Any(s => s == "*" || s.EndsWith('/') ? s == "*" || path.StartsWith(s, StringComparison.Ordinal) : path == s);
    public static void ValidateScopes(string[]? scopes)
    {
        if (scopes is not { Length: > 0 and <= 20 } || scopes.Any(s => s != "*" && !SafeRelative(s.TrimEnd('/')))) throw new ArgumentException("Use explicit root-relative file/directory scopes, or *; hidden, secret and generated paths stay denied.");
    }
    public static void ValidateCommand(CommandSpec? command)
    {
        if (command is null || !Identifiers.IsValid(command.Name) || command.Executable is not ("node" or "npm" or "dotnet" or "python3") ||
            command.Arguments is not { Length: > 0 and <= 24 } || command.Arguments.Any(a => a is null || a.Length > 1000 || a.Any(char.IsControl) || SourceSafety.ContainsKnownSecret(a)) || command.TimeoutSeconds is < 1 or > 300)
            throw new ArgumentException("Commands use node/npm/dotnet/python3 and explicit argument arrays, inside the sandbox only (1–300 seconds).");
    }
    public static WorkflowValidation Compile(SoftwareWorkflow? definition)
    {
        var errors = new List<string>(); var order = new List<string>();
        if (definition is null || !Identifiers.IsValid(definition.Name) || definition.Version is not { Length: > 0 and <= 32 } || definition.Nodes is not { Length: > 0 and <= 32 })
            return new(false, ["Invalid workflow identity or node count."], [], []);
        if (definition.MaxParallel is < 1 or > 4 || definition.MaxAttempts is < 1 or > 3 || definition.MaxElapsedMinutes is < 1 or > 180 || definition.MaxOutputTokens is < 1000 or > 200000) errors.Add("Invalid concurrency/attempt/time/token budget.");
        var nodes = new Dictionary<string, WorkflowNode>();
        foreach (var n in definition.Nodes)
        {
            if (n is null || !Identifiers.IsValid(n.Id) || n.DependsOn is null || !nodes.TryAdd(n.Id, n)) { errors.Add("Duplicate or invalid node."); continue; }
            if (n.Kind is not ("intake" or "agent" or "approval" or "fanout" or "build" or "test" or "security" or "quality" or "gate" or "report" or "release") || n.Role is not null && !Roles.Contains(n.Role)) errors.Add("Unknown node type/role: " + n.Id);
            if (n.When is not ("always" or "release_requested") || n.RunOnFailure && n.Kind != "report") errors.Add("Unsupported condition/failure handler: " + n.Id);
            if (n.OnFail is not null && (n.Kind != "gate" || n.MaxLoop is < 1 or > 2)) errors.Add("Only a bounded quality-gate loop is supported.");
            if (n.MaxLoop != 0 && n.OnFail is null) errors.Add("Loop bound requires onFail.");
        }
        var visited = new HashSet<string>(); var active = new HashSet<string>();
        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            if (!nodes.TryGetValue(id, out var n)) { errors.Add("Missing dependency: " + id); return; }
            if (!active.Add(id)) { errors.Add("Dependency cycle: " + id); return; }
            foreach (var dependency in n.DependsOn) Visit(dependency);
            active.Remove(id); visited.Add(id); order.Add(id);
        }
        foreach (var id in nodes.Keys) Visit(id);
        foreach (var n in nodes.Values.Where(n => n.OnFail is not null))
            if (!nodes.TryGetValue(n.OnFail!, out var target) || target.Kind != "fanout" || order.IndexOf(target.Id) >= order.IndexOf(n.Id)) errors.Add("onFail must target an earlier Dev fanout.");
        if (nodes.Keys.Except(DefaultWorkflow.Nodes.Select(n => n.Id)).Any()) errors.Add("This executable contract supports only its versioned software node schemas; arbitrary extra nodes are not executable.");
        // The executable software contract is versioned; custom definitions may tune budgets and add dependencies,
        // but cannot remove mandatory gates, bypass them or substitute unsupported node schemas.
        foreach (var required in DefaultWorkflow.Nodes)
            if (!nodes.TryGetValue(required.Id, out var n) || n.Kind != required.Kind || n.Role != required.Role || required.DependsOn.Except(n.DependsOn).Any() || n.When != required.When || required.RunOnFailure && !n.RunOnFailure)
                errors.Add("Required software contract node/dependency: " + required.Id);
        var expanded = order.SelectMany(id => nodes[id].Kind is "fanout" or "build" or "test" or "security" or "quality" or "gate"
            ? Enumerable.Range(0, (nodes.GetValueOrDefault("gate")?.MaxLoop ?? 0) + 1).Select(round => id + ":round-" + round) : [id]).ToArray();
        return new(errors.Count == 0, errors.Distinct().ToArray(), order.ToArray(), expanded);
    }
    public static RequirementSpec ValidateSpec(RequirementSpec? spec)
    {
        if (spec is null || string.IsNullOrWhiteSpace(spec.Title) || spec.Title.Length > 200 || spec.Goals is not { Length: > 0 and <= 12 } ||
            spec.Acceptance is not { Length: > 0 and <= 20 } || spec.Assumptions is null || spec.OpenQuestions is null || spec.Assumptions.Length > 20 || spec.OpenQuestions.Length > 20 ||
            spec.Acceptance.Any(c => c is null || !Identifiers.IsValid(c.Id) || string.IsNullOrWhiteSpace(c.Description) || c.Description.Length > 1000 || c.Verification is not ("test" or "manual")) || spec.Acceptance.Select(c => c.Id).Distinct().Count() != spec.Acceptance.Length ||
            spec.Goals.Concat(spec.Assumptions).Concat(spec.OpenQuestions).Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 1000) || SourceSafety.ContainsKnownSecret(JsonSerializer.Serialize(spec, RuntimeJson.Options)))
            throw new ArgumentException("RequirementSpec needs bounded goals, uniquely identified acceptance criteria and explicit test/manual verification.");
        return spec;
    }
    public static TaskGraph ValidatePlan(TaskGraph? plan, RequirementSpec spec, RepositoryProject project, RoleBinding[] bindings)
    {
        if (plan is null || string.IsNullOrWhiteSpace(plan.Summary) || plan.Summary.Length > 2000 || plan.Tasks is not { Length: > 0 and <= 8 } ||
            plan.Tasks.Any(t => t is null || !Identifiers.IsValid(t.Id) || string.IsNullOrWhiteSpace(t.Title) || t.Title.Length > 500 || t.DependsOn is null ||
                t.AllowedPaths is null || t.AcceptanceIds is not { Length: > 0 } || t.AcceptanceIds.Except(spec.Acceptance.Select(c => c.Id)).Any() ||
                !bindings.Any(b => b.RoleId == "dev" && b.Agent.AgentId == t.AgentId)) || plan.Tasks.Select(t => t.Id).Distinct().Count() != plan.Tasks.Length)
            throw new ArgumentException("A TaskGraph needs 1–8 distinct bounded tasks assigned to pinned Dev agents with known acceptance IDs.");
        foreach (var t in plan.Tasks)
        {
            ValidateScopes(t.AllowedPaths);
            if (t.AllowedPaths.Any(p => !ScopeSubset(p, project.WritePaths))) throw new ArgumentException("Tasks cannot expand project write scope.");
        }
        var done = new HashSet<string>();
        for (var round = 0; round < plan.Tasks.Length; round++) foreach (var t in plan.Tasks.Where(t => t.DependsOn.All(done.Contains))) done.Add(t.Id);
        if (done.Count != plan.Tasks.Length || spec.Acceptance.Any(c => !plan.Tasks.Any(t => t.AcceptanceIds.Contains(c.Id)))) throw new ArgumentException("Task dependencies are cyclic/missing or acceptance coverage is incomplete.");
        return plan;
    }
    public static bool ScopeSubset(string scope, string[] allowed) => allowed.Contains("*") || allowed.Any(a => a.EndsWith('/') ? scope.StartsWith(a, StringComparison.Ordinal) : scope == a);
}
