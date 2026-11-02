using System.Text.Json;

namespace AICompanyOS.Runtime;

public sealed record CompanyWorkbench(CompanyProfile Profile, RepositoryProject[] Projects, AgentConfiguration[] AgentConfigurations,
    SkillPackage[] Skills, SoftwareWorkflow[] Workflows, SoftwareRun[] Runs, PublicInteraction[] Interactions, Meeting[] Meetings, ConsultationJob[] Consultations);

public sealed partial class FileEventStore
{
    private static CompanyProfile ProfileFrom(IEnumerable<DomainEvent> events) => events.LastOrDefault(e => e.Type == "company.profile.changed")?.Data.Deserialize<CompanyProfile>(JsonOptions)
        ?? new(0, "Personal software delivery company", ["Research", "Engineering", "Quality", "Operations"]);
    private static RepositoryProject[] ProjectsFrom(IEnumerable<DomainEvent> events) => events.Where(e => e.Type == "project.configured").Select(e => e.Data.Deserialize<RepositoryProject>(JsonOptions)!)
        .GroupBy(p => p.ProjectId).Select(g => g.Last()).OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
    private static AgentConfiguration[] ConfigurationsFrom(IEnumerable<DomainEvent> events) => events.Where(e => e.Type == "agent.configuration.changed")
        .Select(e => e.Data.Deserialize<AgentConfiguration>(JsonOptions)!).GroupBy(p => p.AgentId).Select(g => g.Last()).ToArray();
    private static SkillPackage[] SkillsFrom(IEnumerable<DomainEvent> events) => events.Where(e => e.Type == "skill.installed").Select(e => e.Data.Deserialize<SkillPackage>(JsonOptions)!).ToArray();
    private static SoftwareWorkflow[] WorkflowsFrom(IEnumerable<DomainEvent> events) => new[] { SoftwareContracts.DefaultWorkflow }
        .Concat(events.Where(e => e.Type == "workflow.definition.saved").Select(e => e.Data.Deserialize<SoftwareWorkflow>(JsonOptions)!)).ToArray();
    private static PublicInteraction[] InteractionsFrom(IEnumerable<DomainEvent> events) => events.Where(e => e.Type == "interaction.created").Select(e => e.Data.Deserialize<PublicInteraction>(JsonOptions)!).ToArray();
    private static Meeting[] MeetingsFrom(IEnumerable<DomainEvent> events) => events.Where(e => e.Type is "meeting.created" or "meeting.started" or "meeting.completed")
        .Select(e => e.Data.Deserialize<Meeting>(JsonOptions)!).GroupBy(m => m.MeetingId).Select(g => g.Last()).ToArray();
    private static SoftwareRun[] SoftwareRunsFrom(string company, IEnumerable<DomainEvent> events) => events.GroupBy(e => e.RunId)
        .Select(g => RebuildSoftwareRun(company, g.Key, g.ToArray())).Where(r => r is not null).Cast<SoftwareRun>().OrderByDescending(r => r.LastSequence).ToArray();
    public async Task<CompanyWorkbench> WorkbenchAsync(string company, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(company, 0, ct);
            return new(ProfileFrom(events), ProjectsFrom(events), ConfigurationsFrom(events), SkillsFrom(events), WorkflowsFrom(events),
                SoftwareRunsFrom(company, events), InteractionsFrom(events), MeetingsFrom(events), ConsultationsFrom(events));
        }
        finally { _gate.Release(); }
    }
    public async Task<CompanyProfile> ConfigureCompanyAsync(string company, CompanyProfileRequest request, CancellationToken ct)
    {
        if (request.Description is null || request.Description.Length > 2000 || SourceSafety.ContainsKnownSecret(request.Description) ||
            request.Departments is not { Length: > 0 and <= 12 } || request.Departments.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 80) || request.Departments.Distinct().Count() != request.Departments.Length)
            throw new ArgumentException("Choose 1–12 departments and a bounded, secret-free description.");
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(company, 0, ct); var profile = ProfileFrom(events);
            if (profile.Version != request.ExpectedVersion) throw new WorkspaceConflictException("Company settings changed.");
            var updated = new CompanyProfile(profile.Version + 1, request.Description, request.Departments, request.AllowGitHubRelease);
            await AppendUnsafeAsync(company, "company-settings", [DomainEvent.Create("company.profile.changed", updated)], events, ct); return updated;
        }
        finally { _gate.Release(); }
    }
    public async Task<RepositoryProject> ConfigureProjectAsync(string company, ProjectRequest request, RepositoryService repositories, CancellationToken ct)
    {
        if (!Identifiers.IsValid(request.ProjectId) || request.ProjectId.Length > 64 || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 120 || !Path.IsPathFullyQualified(request.RepositoryPath) || request.RepositoryPath.Any(char.IsControl) ||
            request.DockerImage is not { Length: > 0 and <= 160 } || !request.DockerImage.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '/' or ':' or '@') ||
            !Identifiers.IsValid(request.BaseBranch)) throw new ArgumentException("A project needs an ID, local repository path, installed image and simple base branch.");
        SoftwareContracts.ValidateScopes(request.ReadPaths); SoftwareContracts.ValidateScopes(request.WritePaths);
        if (request.WritePaths.Any(p => !SoftwareContracts.ScopeSubset(p, request.ReadPaths))) throw new ArgumentException("Write scopes must be contained in read scopes.");
        SoftwareContracts.ValidateCommand(request.Build); SoftwareContracts.ValidateCommand(request.Test);
        if (request.RemoteUrl is not null && !RepositoryService.ValidGitHubRemote(request.RemoteUrl)) throw new ArgumentException("Remote release supports a credential-free https://github.com/owner/repository.git destination.");
        await repositories.ValidateRepositoryAsync(request.RepositoryPath, ct);
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(company, 0, ct); var prior = ProjectsFrom(events).FirstOrDefault(p => p.ProjectId == request.ProjectId);
            if ((prior?.Version ?? 0) != request.ExpectedVersion) throw new WorkspaceConflictException("Project changed; reload its version.");
            if (prior is null && ProjectsFrom(events).Length >= 40) throw new WorkspaceConflictException("This profile supports 40 projects per company.");
            var project = new RepositoryProject(request.ProjectId, request.ExpectedVersion + 1, request.Name.Trim(), Path.GetFullPath(request.RepositoryPath), request.ReadPaths, request.WritePaths,
                request.DockerImage, request.Build, request.Test, request.RemoteUrl, request.BaseBranch);
            await AppendUnsafeAsync(company, project.ProjectId, [DomainEvent.Create("project.configured", project)], events, ct); return project;
        }
        finally { _gate.Release(); }
    }
    public async Task<AgentConfiguration> ConfigureAgentAsync(string company, string agentId, AgentConfigurationRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Department) || request.Department.Length > 80 || request.ModelId is not null && !OllamaProvider.ValidModelName(request.ModelId) ||
            request.SkillRefs is not { Length: <= 12 } || request.SkillRefs.Any(s => s is null || s.Length > 100) || request.MaxInputBytes is < 4000 or > 10000 || request.MaxOutputTokens is < 256 or > 4096)
            throw new ArgumentException("Invalid department, installed-model ID, skill references or bounded input/output budget.");
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(company, 0, ct);
            var agent = AgentsFrom(company, await ReadEventsUnsafeAsync(CatalogCompany, 0, ct), events).FirstOrDefault(a => a.AgentId == agentId) ?? throw new KeyNotFoundException();
            var prior = ConfigurationsFrom(events).FirstOrDefault(a => a.AgentId == agentId);
            if ((prior?.Version ?? 0) != request.ExpectedVersion) throw new WorkspaceConflictException("Agent configuration changed.");
            var skills = SkillsFrom(events);
            if (request.SkillRefs.Any(r => !skills.Any(s => s.SkillId + "@" + s.Version == r && s.Roles.Intersect(agent.RoleIds).Any()))) throw new ArgumentException("Every skill reference must resolve to an installed version compatible with the agent's roles.");
            var configured = new AgentConfiguration(agentId, request.ExpectedVersion + 1, request.Department, request.ModelId, request.SkillRefs.Distinct().ToArray(), request.MaxInputBytes, request.MaxOutputTokens);
            await AppendUnsafeAsync(company, agentId, [DomainEvent.Create("agent.configuration.changed", configured)], events, ct); return configured;
        }
        finally { _gate.Release(); }
    }
    public async Task<SkillPackage> InstallSkillAsync(string company, SkillPackage skill, CancellationToken ct)
    {
        if (!Identifiers.IsValid(skill.SkillId) || skill.Version is not { Length: > 0 and <= 32 } || !skill.Version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') ||
            string.IsNullOrWhiteSpace(skill.Name) || skill.Name.Length > 120 || string.IsNullOrWhiteSpace(skill.Instructions) || skill.Instructions.Length > 8000 || SourceSafety.ContainsKnownSecret(skill.Instructions) ||
            !BuiltInCatalog.ValidRoles(skill.Roles) || skill.RequestedTools is not { Length: <= 12 } || skill.RequestedTools.Any(s => s is not ("repo.read" or "worktree.write.scoped" or "container.build" or "container.test" or "report.read")))
            throw new ArgumentException("Install a versioned instruction package with known roles/tools; packages never grant or execute tools.");
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(company, 0, ct); var prior = SkillsFrom(events).FirstOrDefault(s => s.SkillId == skill.SkillId && s.Version == skill.Version);
            if (prior is not null) return RuntimeJson.Hash(prior) == RuntimeJson.Hash(skill) ? prior : throw new WorkspaceConflictException("Installed skill versions are immutable.");
            await AppendUnsafeAsync(company, skill.SkillId, [DomainEvent.Create("skill.installed", skill)], events, ct); return skill;
        }
        finally { _gate.Release(); }
    }
    public async Task<WorkflowValidation> SaveWorkflowAsync(string company, SoftwareWorkflow workflow, bool dryRun, CancellationToken ct)
    {
        var result = SoftwareContracts.Compile(workflow);
        if (!result.Valid || dryRun) return result;
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(company, 0, ct); var prior = WorkflowsFrom(events).FirstOrDefault(w => w.Name == workflow.Name && w.Version == workflow.Version);
            if (prior is not null && RuntimeJson.Hash(prior) != RuntimeJson.Hash(workflow)) throw new WorkspaceConflictException("Workflow versions are immutable; choose a new version.");
            if (prior is null) await AppendUnsafeAsync(company, workflow.Name, [DomainEvent.Create("workflow.definition.saved", workflow)], events, ct);
            return result;
        }
        finally { _gate.Release(); }
    }
    private static void RequireConversationRun(string company, string run, IReadOnlyList<DomainEvent> events)
    {
        if (run != "lobby" && !events.Any(e => e.RunId == run && e.Type is "software.created" or "workflow.created")) throw new KeyNotFoundException("Conversation scope not found in this company.");
    }
    public async Task<PublicInteraction> AddInteractionAsync(string company, InteractionRequest request, string key, CancellationToken ct)
    {
        if (!Identifiers.IsValid(request.RunId) || request.Type is not ("inform" or "consult" or "peer_review" or "escalation" or "status" or "question" or "answer") ||
            request.ToAgentIds is not { Length: > 0 and <= 8 } || string.IsNullOrWhiteSpace(request.Summary) || request.Summary.Length > 2000 || SourceSafety.ContainsKnownSecret(request.Summary) || request.ArtifactRefs is not { Length: <= 20 } || request.Citations is not { Length: <= 20 })
            throw new ArgumentException("Invalid public interaction; private reasoning and credentials must not be supplied.");
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(company, 0, ct); RequireConversationRun(company, request.RunId, events);
            var agents = AgentsFrom(company, await ReadEventsUnsafeAsync(CatalogCompany, 0, ct), events);
            if (request.FromAgentId != "owner" && !agents.Any(a => a.AgentId == request.FromAgentId) || request.ToAgentIds.Any(id => id != "owner" && !agents.Any(a => a.AgentId == id))) throw new ArgumentException("Interaction participants must belong to this company.");
            if (request.Citations.Length > 0) ValidateCitations(request.Citations, SourcesFrom(events));
            if (request.ReplyTo is not null && !InteractionsFrom(events).Any(i => i.InteractionId == request.ReplyTo && i.RunId == request.RunId)) throw new ArgumentException("Reply does not resolve in this thread.");
            var software = RebuildSoftwareRun(company, request.RunId, events.Where(e => e.RunId == request.RunId).ToArray());
            var legacy = RebuildRun(company, request.RunId, events.Where(e => e.RunId == request.RunId).ToArray());
            var refs = MeetingsFrom(events).Where(m => m.RunId == request.RunId).Select(m => m.MeetingId)
                .Concat(software?.Report.ArtifactRefs ?? []).Concat(software?.Receipts.Select(r => r.ReceiptId) ?? [])
                .Concat(new[] { legacy?.Spec?.ArtifactId, legacy?.Plan?.ArtifactId, legacy?.Handoff?.HandoffId }.Where(s => s is not null).Cast<string>()).ToHashSet(StringComparer.Ordinal);
            if (request.ArtifactRefs.Any(r => r is null || !refs.Contains(r))) throw new ArgumentException("Artifact references must resolve in this company and thread.");
            var prior = InteractionsFrom(events).FirstOrDefault(i => i.DedupeKey == key);
            if (prior is not null) return prior.Summary == request.Summary && prior.RunId == request.RunId && prior.Type == request.Type && prior.FromAgentId == request.FromAgentId && prior.ToAgentIds.SequenceEqual(request.ToAgentIds)
                && prior.ReplyTo == request.ReplyTo && RuntimeJson.Hash(prior.ArtifactRefs) == RuntimeJson.Hash(request.ArtifactRefs) && RuntimeJson.Hash(prior.Citations) == RuntimeJson.Hash(request.Citations) ? prior : throw new WorkspaceConflictException("Interaction key belongs to another payload.");
            var interaction = new PublicInteraction("message-" + Guid.NewGuid().ToString("N"), request.RunId, request.Type, request.FromAgentId, request.ToAgentIds, request.Summary,
                request.ArtifactRefs, request.Citations, request.ReplyTo, "recorded", key, DateTimeOffset.UtcNow);
            await AppendUnsafeAsync(company, request.RunId, [DomainEvent.Create("interaction.created", interaction)], events, ct); return interaction;
        }
        finally { _gate.Release(); }
    }
    public async Task<Meeting> CreateMeetingAsync(string company, MeetingRequest request, string key, CancellationToken ct)
    {
        if (!Identifiers.IsValid(request.RunId) || string.IsNullOrWhiteSpace(request.Agenda) || request.Agenda.Length > 2000 || SourceSafety.ContainsKnownSecret(request.Agenda) || request.Participants is not { Length: >= 2 and <= 8 } || request.Participants.Distinct().Count() != request.Participants.Length)
            throw new ArgumentException("A meeting needs 2–8 distinct agents and a bounded public agenda.");
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(company, 0, ct); RequireConversationRun(company, request.RunId, events);
            var agents = AgentsFrom(company, await ReadEventsUnsafeAsync(CatalogCompany, 0, ct), events);
            if (request.Participants.Any(id => !agents.Any(a => a.AgentId == id))) throw new ArgumentException("Meeting participants must belong to this company.");
            var prior = events.FirstOrDefault(e => e.Type == "meeting.command" && ReadString(e.Data, "key") == key);
            if (prior is not null) return ReadString(prior.Data, "hash") == RuntimeJson.Hash(request) ? MeetingsFrom(events).Single(m => m.MeetingId == ReadString(prior.Data, "meetingId")) : throw new WorkspaceConflictException("Meeting key belongs to a different request.");
            var meeting = new Meeting("meeting-" + Guid.NewGuid().ToString("N"), request.RunId, request.Agenda, request.Participants, "scheduled", null, [], [], 1);
            await AppendUnsafeAsync(company, request.RunId, [DomainEvent.Create("meeting.created", meeting), DomainEvent.Create("meeting.command", new { key, hash = RuntimeJson.Hash(request), meetingId = meeting.MeetingId })], events, ct); return meeting;
        }
        finally { _gate.Release(); }
    }
    public async Task<Meeting> ChangeMeetingAsync(string company, string meetingId, bool start, MeetingCompleteRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(company, 0, ct); var meeting = MeetingsFrom(events).FirstOrDefault(m => m.MeetingId == meetingId) ?? throw new KeyNotFoundException();
            if (meeting.Version != request.ExpectedVersion || meeting.State == "completed" || start && meeting.State != "scheduled" || !start && meeting.State != "started") throw new WorkspaceConflictException("Meeting changed or transition is invalid.");
            if (!start && (string.IsNullOrWhiteSpace(request.Summary) || request.Summary.Length > 4000 || SourceSafety.ContainsKnownSecret(request.Summary) || request.ActionItems is not { Length: <= 20 } || request.ActionItems.Any(s => s is null || s.Length > 1000 || SourceSafety.ContainsKnownSecret(s)) || request.Citations is not { Length: <= 20 })) throw new ArgumentException("Supply bounded public minutes and action items.");
            if (!start && request.Citations.Length > 0) ValidateCitations(request.Citations, SourcesFrom(events));
            var updated = meeting with { State = start ? "started" : "completed", Summary = start ? null : request.Summary, ActionItems = start ? [] : request.ActionItems, Citations = start ? [] : request.Citations, Version = meeting.Version + 1 };
            var additions = new List<DomainEvent> { DomainEvent.Create(start ? "meeting.started" : "meeting.completed", updated) };
            if (!start)
            {
                var text = updated.Summary + "\n" + string.Join('\n', updated.ActionItems);
                additions.Add(DomainEvent.Create("source.imported", new SourceRevision(meeting.MeetingId, 1, "Meeting minutes: " + meeting.Agenda[..Math.Min(meeting.Agenda.Length, 120)], text, RuntimeJson.TextHash(text), DateTimeOffset.UtcNow)));
            }
            await AppendUnsafeAsync(company, meeting.RunId, additions, events, ct); return updated;
        }
        finally { _gate.Release(); }
    }
}
