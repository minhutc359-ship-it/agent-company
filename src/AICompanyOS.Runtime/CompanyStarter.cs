using System.Text.Json;

namespace AICompanyOS.Runtime;

public sealed record StarterAgent(string AgentId, string Name, string[] RoleIds, string Department, string[] SkillRefs, string Purpose);
public sealed record CompanyStarterPack(string PackId, string Version, string Name, string Description, StarterAgent[] Agents, SkillPackage[] Skills, SoftwareWorkflow[] Workflows);
public sealed record StarterInstallRequest(string PackId, string? ModelId = null);
public sealed record StarterInstallResult(string PackId, int AddedAgents, int ConfiguredAgents, int InstalledSkills, int SavedWorkflows, string[] PreservedAgents);

public static class CompanyStarters
{
    public static CompanyStarterPack SoftwareTeam => new("software-team", "1.0.0", "Đội phát triển phần mềm", "8 nhân viên, 7 vai trò, kỹ năng theo vai trò và 3 workflow để bắt đầu. Bạn có thể sửa từng cấu hình sau khi cài.",
    [
        Member("ba", "Business Analyst", "Research", "Làm rõ mục tiêu, ràng buộc và tiêu chí có thể kiểm thử."),
        Member("leader", "Delivery Leader", "Engineering", "Chia công việc, kiểm tra phụ thuộc và phạm vi ghi."),
        Member("dev", "Developer", "Engineering", "Triển khai patch nhỏ theo yêu cầu và phạm vi đã duyệt."),
        new("dev-support", "Developer Support", ["dev"], "Engineering", ["starter-dev@1.0.0"], "Nhận task độc lập, tài liệu hoặc test song song với Dev chính."),
        Member("tester", "Tester", "Quality", "Chạy test thật và báo lỗi tái hiện được."),
        Member("soc", "Security Reviewer", "Quality", "Đối chiếu secrets, dependency, phạm vi và finding bảo mật."),
        Member("qa", "Quality Reviewer", "Quality", "Kiểm tra tiêu chí nghiệm thu, provenance và license evidence."),
        Member("subpm", "Delivery Reporter", "Operations", "Tóm tắt bằng chứng, rủi ro và việc cần Owner quyết định.")
    ],
    [
        Skill("ba", "Phân tích yêu cầu", "Produce RequirementSpec JSON from the pinned objective and approved source context. Separate facts from assumptions. Define bounded measurable acceptance criteria with verification=test or manual. Preserve every accepted constraint. State unresolved questions; do not invent evidence or grant tools.", ["repo.read"]),
        Skill("leader", "Lập kế hoạch & phân công", "Produce TaskGraph JSON with acceptance coverage, dependencies, explicit allowedPaths and pinned Dev agent IDs. Keep independent tasks in disjoint writable scopes where possible. Never expand the approved project scope. Request Owner clarification for missing requirements. Do not approve any Owner gate.", ["repo.read"]),
        Skill("dev", "Triển khai có kiểm chứng", "Produce RepositoryPatch JSON only for the assigned task. Read the complete pinned file before editing; use exact beforeSha256. Implement a small complete change, add meaningful tests, preserve existing behavior outside the task, and use actual failure receipts for correction. Request missing pinned files through readRequests. Never claim a command ran from prose, download dependencies, push, merge or deploy.", ["repo.read", "worktree.write.scoped"]),
        Skill("tester", "Kiểm thử và hồi quy", "Use actual container test receipts and explicit acceptance criteria. Distinguish failed, blocked and not-run. Explain a reproducible failure with its file and expected behavior; never claim PASS without runtime evidence. Do not change Owner-approved scope or replace evidence with model confidence.", ["repo.read", "container.test"]),
        Skill("soc", "Rà soát an toàn", "Inspect the pinned candidate, secret findings and inventory-matching dependency audit evidence. Distinguish evidence from assumptions. Escalate critical findings and missing audits. Do not approve your own critical finding, expose credentials or grant network/tools.", ["repo.read", "report.read"]),
        Skill("qa", "Nghiệm thu & nguồn gốc", "Map each acceptance criterion to actual evidence or an explicit manual Owner check. Review license/provenance evidence and flag unsupported claims. Do not treat unknown licenses as safe. Preserve required gates and never mark the Owner's semantic acceptance on their behalf.", ["repo.read", "report.read"]),
        Skill("subpm", "Báo cáo & bàn giao", "Summarize only persisted spec, task, patch and runtime receipts. Include passed/failed/not-run status, unresolved risks, token usage when reported, outstanding approvals and exact artifact references. Never infer execution from agent prose or approve release. Keep the report public and credential-free.", ["report.read"])
    ],
    [
        SoftwareContracts.DefaultWorkflow with { Version = "2.3.0" },
        QualityWorkflow(),
        SoftwareContracts.DefaultWorkflow with { Name = "software-focused", Version = "1.0.0", MaxParallel = 1, MaxAttempts = 2, MaxElapsedMinutes = 20, MaxOutputTokens = 24000,
            Nodes = SoftwareContracts.DefaultWorkflow.Nodes.Select(n => n.Id == "gate" ? n with { MaxLoop = 1 } : n).ToArray() }
    ]);

    private static StarterAgent Member(string role, string name, string department, string purpose) => new(role + "-agent", name, [role], department, ["starter-" + role + "@1.0.0"], purpose);
    private static SkillPackage Skill(string role, string name, string instructions, string[] tools) => new("starter-" + role, "1.0.0", name, instructions, [role], tools);
    private static SoftwareWorkflow QualityWorkflow() => SoftwareContracts.DefaultWorkflow with
    {
        Name = "software-reviewed", Version = "1.0.0", MaxParallel = 2, MaxElapsedMinutes = 45, MaxOutputTokens = 60000,
        Nodes = SoftwareContracts.DefaultWorkflow.Nodes.Select(n => n.Id switch
        {
            "soc" => n with { DependsOn = ["build", "tester"] },
            "qa" => n with { DependsOn = ["build", "soc"] },
            _ => n
        }).ToArray()
    };
}

public sealed partial class FileEventStore
{
    public async Task<StarterInstallResult> InstallCompanyStarterAsync(string company, StarterInstallRequest request, string key, LocalModel[] models, CancellationToken ct)
    {
        var pack = CompanyStarters.SoftwareTeam;
        if (request.PackId != pack.PackId || !Identifiers.IsValid(key) || request.ModelId is not null && (!OllamaProvider.ValidModelName(request.ModelId) || !models.Any(m => m.Name == request.ModelId)))
            throw new ArgumentException("Choose the known starter pack and an installed local model, or leave the model empty.");
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(company, 0, ct); var hash = RuntimeJson.Hash(request);
            var replay = events.FirstOrDefault(e => e.Type == "company.starter.applied" && ReadString(e.Data, "key") == key);
            if (replay is not null) return ReadString(replay.Data, "hash") == hash ? replay.Data.GetProperty("result").Deserialize<StarterInstallResult>(JsonOptions)! : throw new WorkspaceConflictException("Starter command key belongs to another request.");
            var agents = AgentsFrom(company, await ReadEventsUnsafeAsync(CatalogCompany, 0, ct), events);
            var configured = ConfigurationsFrom(events); var skills = SkillsFrom(events); var workflows = WorkflowsFrom(events); var profile = ProfileFrom(events);
            var pending = new List<DomainEvent>(); var preserved = new List<string>(); var added = 0; var configs = 0; var installed = 0; var saved = 0;
            if (agents.Length + pack.Agents.Count(a => !agents.Any(existing => existing.AgentId == a.AgentId)) > 32) throw new WorkspaceConflictException("Starter would exceed the company agent limit.");
            foreach (var skill in pack.Skills)
            {
                var prior = skills.FirstOrDefault(s => s.SkillId == skill.SkillId && s.Version == skill.Version);
                if (prior is not null && RuntimeJson.Hash(prior) != RuntimeJson.Hash(skill)) throw new WorkspaceConflictException("A starter skill version conflicts with an existing immutable package.");
                if (prior is null) { pending.Add(DomainEvent.Create("skill.installed", skill)); installed++; }
            }
            foreach (var workflow in pack.Workflows)
            {
                if (!SoftwareContracts.Compile(workflow).Valid) throw new InvalidOperationException("Invalid built-in starter workflow.");
                var prior = workflows.FirstOrDefault(w => w.Name == workflow.Name && w.Version == workflow.Version);
                if (prior is not null && RuntimeJson.Hash(prior) != RuntimeJson.Hash(workflow)) throw new WorkspaceConflictException("A starter workflow version conflicts with an existing definition.");
                if (prior is null) { pending.Add(DomainEvent.Create("workflow.definition.saved", workflow)); saved++; }
            }
            foreach (var member in pack.Agents)
            {
                var agent = agents.FirstOrDefault(a => a.AgentId == member.AgentId);
                if (agent is null) { agent = new(member.AgentId, member.Name, member.RoleIds, 1, []); pending.Add(DomainEvent.Create("agent.created", agent)); added++; }
                if (configured.Any(c => c.AgentId == agent.AgentId) || !member.RoleIds.All(agent.RoleIds.Contains)) { preserved.Add(agent.Name); continue; }
                var department = profile.Departments.Contains(member.Department) ? member.Department : profile.Departments[0];
                pending.Add(DomainEvent.Create("agent.configuration.changed", new AgentConfiguration(agent.AgentId, 1, department, request.ModelId, member.SkillRefs, 10000, 4096))); configs++;
            }
            var result = new StarterInstallResult(pack.PackId, added, configs, installed, saved, preserved.ToArray());
            pending.Add(DomainEvent.Create("company.starter.applied", new { key, hash, pack.Version, result }));
            await AppendUnsafeAsync(company, "company-starter", pending, events, ct); return result;
        }
        finally { _gate.Release(); }
    }
}
