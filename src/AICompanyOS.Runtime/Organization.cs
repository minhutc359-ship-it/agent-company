using System.Text.Json;

namespace AICompanyOS.Runtime;

public sealed record Company(string CompanyId, string Name, string PrivacyMode, DateTimeOffset CreatedAt);
public sealed record CreateCompanyRequest(string CompanyId, string Name);
public sealed record Agent(string AgentId, string Name, string[] RoleIds, int Version, string[] ToolGrants, string? ModelRoute = null);
public sealed record CreateAgentRequest(string AgentId, string Name, string[] RoleIds);
public sealed record AssignRolesRequest(string[] RoleIds, int ExpectedVersion);
public sealed record RoleContract(string RoleId, string Version, string Name, string InputSchema, string OutputSchema, string[] SkillIds, string[] ForbiddenActions);
public sealed record SkillContract(string SkillId, string Version, string Name, string[] RequestedTools);

public static class BuiltInCatalog
{
    public static readonly RoleContract[] Roles =
    [
        new("ba", "1.0.0", "Business Analyst", "RequestContextV1", "RequirementSpecV1", ["research", "spec"], ["approve-requirements", "grant-tools"]),
        new("leader", "1.0.0", "Delivery Leader", "RequirementSpecV1", "DeliveryPlanV1", ["plan", "assign"], ["grant-tools", "approve-owner-gate"]),
        new("dev", "1.0.0", "Developer", "DeliveryPlanV1", "PatchManifestV1", ["cook"], ["git.push", "git.merge", "production.deploy"]),
        new("tester", "1.0.0", "Tester", "PatchManifestV1", "TestReportV1", ["test"], ["claim-pass-without-receipt"]),
        new("soc", "1.0.0", "Security Reviewer", "PatchManifestV1", "SecurityReportV1", ["security-review"], ["approve-own-critical-finding"]),
        new("qa", "1.0.0", "Quality Reviewer", "RequirementSpecV1", "QualityPolicyReportV1", ["check-policy"], ["claim-license-without-provenance"]),
        new("subpm", "1.0.0", "Delivery Reporter", "EvidenceBundleV1", "DeliveryReportV1", ["report", "handoff"], ["verify-from-agent-prose", "approve-owner-gate"])
    ];
    public static readonly SkillContract[] Skills = Roles.SelectMany(r => r.SkillIds).Distinct()
        .Select(id => new SkillContract(id, "1.0.0", id, [])).ToArray();
    public static Agent[] DefaultAgents() => Roles.Select(r => new Agent(r.RoleId + "-agent", r.Name, [r.RoleId], 1, [])).ToArray();
    public static bool ValidRoles(string[]? roles) => roles is { Length: > 0 and <= 7 } &&
        roles.Distinct().Count() == roles.Length && roles.All(id => Roles.Any(r => r.RoleId == id));
}

public sealed partial class FileEventStore
{
    private const string CatalogCompany = "__profile";
    public async Task InitializeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var catalog = await ReadEventsUnsafeAsync(CatalogCompany, 0, ct);
            var legacyCompanies = _journal.Companies().Where(id => id != CatalogCompany).ToArray();
            foreach (var id in legacyCompanies.Append("software-studio").Distinct())
            {
                if (catalog.Any(e => e.Type == "company.created" && e.RunId == id)) continue;
                var company = new Company(id, id == "software-studio" ? "Software Delivery Studio" : id, "strict_local", DateTimeOffset.UtcNow);
                await AppendUnsafeAsync(CatalogCompany, id, [DomainEvent.Create("company.created", new { company, agents = BuiltInCatalog.DefaultAgents() })], catalog, ct);
            }
        }
        finally { _gate.Release(); }
    }

    private static Company[] CompaniesFrom(IEnumerable<DomainEvent> events) => events.Where(e => e.Type == "company.created")
        .Select(e => e.Data.GetProperty("company").Deserialize<Company>(JsonOptions)!).OrderBy(c => c.Name).ToArray();

    public async Task<Company[]> ListCompaniesAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var companies = CompaniesFrom(await ReadEventsUnsafeAsync(CatalogCompany, 0, ct));
            var result = new List<Company>();
            foreach (var company in companies) result.Add(company with { PrivacyMode = ProfileFrom(await ReadEventsUnsafeAsync(company.CompanyId, 0, ct)).PrivacyMode });
            return result.ToArray();
        }
        finally { _gate.Release(); }
    }
    public async Task<bool> CompanyExistsAsync(string id, CancellationToken ct) => id != CatalogCompany &&
        CompaniesFrom(await ReadEventsAsync(CatalogCompany, 0, ct)).Any(c => c.CompanyId == id);

    public async Task<Company> CreateCompanyAsync(CreateCompanyRequest request, CancellationToken ct)
    {
        if (!Identifiers.IsValid(request.CompanyId) || request.CompanyId.StartsWith("__") ||
            string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 120) throw new ArgumentException("Invalid company ID or name.");
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(CatalogCompany, 0, ct);
            var existing = CompaniesFrom(events).FirstOrDefault(c => string.Equals(c.CompanyId, request.CompanyId, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return existing.CompanyId == request.CompanyId && existing.Name == request.Name.Trim() ? existing : throw new WorkspaceConflictException("Company ID already exists; IDs differing only by letter case are not portable.");
            var company = new Company(request.CompanyId, request.Name.Trim(), "strict_local", DateTimeOffset.UtcNow);
            await AppendUnsafeAsync(CatalogCompany, company.CompanyId,
                [DomainEvent.Create("company.created", new { company, agents = BuiltInCatalog.DefaultAgents() })], events, ct);
            return company;
        }
        finally { _gate.Release(); }
    }

    private static Agent[] AgentsFrom(string companyId, IReadOnlyList<DomainEvent> catalog, IReadOnlyList<DomainEvent> events)
    {
        var seed = catalog.FirstOrDefault(e => e.Type == "company.created" && e.RunId == companyId);
        var agents = seed?.Data.GetProperty("agents").Deserialize<Agent[]>(JsonOptions)?.ToDictionary(a => a.AgentId)
            ?? new Dictionary<string, Agent>();
        foreach (var item in events.Where(e => e.Type is "agent.created" or "agent.roles.changed"))
        {
            var agent = item.Data.Deserialize<Agent>(JsonOptions)!;
            agents[agent.AgentId] = agent;
        }
        return agents.Values.OrderBy(a => a.AgentId, StringComparer.Ordinal).ToArray();
    }

    public async Task<Agent[]> ListAgentsAsync(string companyId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return AgentsFrom(companyId, await ReadEventsUnsafeAsync(CatalogCompany, 0, ct), await ReadEventsUnsafeAsync(companyId, 0, ct)); }
        finally { _gate.Release(); }
    }

    public async Task<Agent> CreateAgentAsync(string companyId, CreateAgentRequest request, CancellationToken ct)
    {
        if (!Identifiers.IsValid(request.AgentId) || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 120 || !BuiltInCatalog.ValidRoles(request.RoleIds))
            throw new ArgumentException("Valid agent ID, name and known roles are required.");
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(companyId, 0, ct);
            var agents = AgentsFrom(companyId, await ReadEventsUnsafeAsync(CatalogCompany, 0, ct), events);
            var prior = agents.FirstOrDefault(a => a.AgentId == request.AgentId);
            if (prior is not null) return prior.Name == request.Name.Trim() && prior.RoleIds.SequenceEqual(request.RoleIds)
                ? prior : throw new WorkspaceConflictException("Agent ID already exists.");
            if (agents.Length >= 32) throw new WorkspaceConflictException("This milestone supports up to 32 agents per company.");
            var agent = new Agent(request.AgentId, request.Name.Trim(), request.RoleIds, 1, []);
            await AppendUnsafeAsync(companyId, request.AgentId, [DomainEvent.Create("agent.created", agent)], events, ct);
            return agent;
        }
        finally { _gate.Release(); }
    }

    public async Task<Agent> AssignRolesAsync(string companyId, string agentId, AssignRolesRequest request, CancellationToken ct)
    {
        if (!BuiltInCatalog.ValidRoles(request.RoleIds)) throw new ArgumentException("Choose known roles without duplicates.");
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(companyId, 0, ct);
            var agent = AgentsFrom(companyId, await ReadEventsUnsafeAsync(CatalogCompany, 0, ct), events).FirstOrDefault(a => a.AgentId == agentId)
                ?? throw new KeyNotFoundException("Agent not found.");
            if (agent.Version != request.ExpectedVersion) throw new WorkspaceConflictException("Agent changed; reload its version.");
            // Assignments never union tool grants and never confer the local Owner capability.
            var updated = agent with { RoleIds = request.RoleIds, Version = agent.Version + 1 };
            await AppendUnsafeAsync(companyId, agentId, [DomainEvent.Create("agent.roles.changed", updated)], events, ct);
            return updated;
        }
        finally { _gate.Release(); }
    }
}
