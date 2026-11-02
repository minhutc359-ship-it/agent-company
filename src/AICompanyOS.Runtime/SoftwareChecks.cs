using System.Text.Json;
using System.Text.RegularExpressions;

namespace AICompanyOS.Runtime;

public sealed record DependencyItem(string Name, string Version, string Path);
public sealed record DependencyLicense(string Name, string Version, string License, string Source);
public sealed record DependencyEvidence(string InventorySha256, DateTimeOffset GeneratedAt, string Tool, string ToolVersion, RuntimeFinding[] Findings, DependencyLicense[] Licenses);

public static partial class SoftwareChecks
{
    public static bool CanRepair(SoftwareRun run)
    {
        var receipts = CurrentReceipts(run);
        if (receipts.Any(r => r.Findings.Any(f => f.Id == "dependency-evidence" || f.Id.StartsWith("license-", StringComparison.Ordinal)))) return false;
        var failedBuild = receipts.Any(r => r.Tool == "docker." + run.Seed.Project.Build.Name && r.Status == "failed" && r.ExitCode is not (null or 126 or 127));
        if (receipts.Any(r => r.Status is "blocked" or "cancelled" || r.Status == "not_run" && !(r.RoleId == "tester" && failedBuild))) return false;
        return failedBuild || receipts.Any(r => r.Status == "failed" && r.ExitCode is not (null or 126 or 127) || r.Tool == "builtin.soc" && r.Findings.Any(f => f.Id.StartsWith("active-code-", StringComparison.Ordinal)));
    }
    [GeneratedRegex(@"\b(?:eval\s*\(|new\s+Function\s*\(|Process\.Start\s*\(|(?:exec|execSync)\s*\()", RegexOptions.CultureInvariant, 100)]
    private static partial Regex DangerousCode();
    public static ExecutionReceipt[] CurrentReceipts(SoftwareRun run) => run.Candidate is null ? [] : run.Receipts.Where(r => r.InputSha256 == run.Candidate.Sha256 &&
        run.Jobs.Any(j => j.JobId == r.JobId && j.Round == run.Round && (r.Attempt == 0 || r.Attempt == j.Attempts))).ToArray();
    public static bool GatesPass(SoftwareRun run)
    {
        if (run.Candidate is null) return false;
        foreach (var node in new[] { "build", "tester", "soc", "qa" })
        {
            var job = run.Jobs.LastOrDefault(j => j.Round == run.Round && j.NodeId == node);
            if (job is null || job.Status != "completed") return false;
            var receipt = CurrentReceipts(run).LastOrDefault(r => r.JobId == job.JobId);
            if (receipt is null || receipt.Status != "executed" || receipt.ExitCode != 0 || receipt.AgentId != job.AgentId || receipt.RoleId != job.RoleId || receipt.Attempt != job.Attempts || receipt.StartedAt > receipt.FinishedAt || receipt.Findings.Any(f => f.Severity is "critical" or "high")) return false;
            if (node is "build" or "tester")
            {
                var command = node == "build" ? run.Seed.Project.Build : run.Seed.Project.Test;
                if (receipt.Tool != "docker." + command.Name || receipt.ToolVersion != "1.0.0" || receipt.ImageId != run.Grant?.ImageId || receipt.Command is null || RuntimeJson.Hash(receipt.Command) != RuntimeJson.Hash(command) || receipt.ArchiveSha256 is null) return false;
            }
            else if (receipt.Tool != "builtin." + node || receipt.ToolVersion != "2.0.0") return false;
            if (receipt.LogSha256 != LogHash(receipt.Stdout, receipt.Stderr, receipt.Findings, receipt.ArchiveSha256, receipt.Command)) return false;
        }
        return true;
    }
    public static string LogHash(string stdout, string stderr, RuntimeFinding[] findings, string? archiveHash, CommandSpec? command) => RuntimeJson.Hash(new { stdout, stderr, findings, archiveHash, command });
    public static DependencyItem[] Dependencies(RepositoryFile[] files)
    {
        var items = new List<DependencyItem>();
        foreach (var file in files.Where(f => f.Path.EndsWith("package-lock.json", StringComparison.Ordinal) || f.Path.EndsWith("packages.lock.json", StringComparison.Ordinal) || f.Path.EndsWith("package.json", StringComparison.Ordinal)))
        {
            try
            {
                using var document = JsonDocument.Parse(file.Content); var root = document.RootElement;
                if (file.Path.EndsWith("package-lock.json", StringComparison.Ordinal) && root.TryGetProperty("packages", out var packages))
                    foreach (var package in packages.EnumerateObject().Where(p => p.Name.Contains("node_modules/", StringComparison.Ordinal)))
                        items.Add(new(package.Name[(package.Name.LastIndexOf("node_modules/", StringComparison.Ordinal) + 13)..], package.Value.TryGetProperty("version", out var version) ? version.GetString() ?? "unknown" : "unknown", file.Path));
                else if (file.Path.EndsWith("packages.lock.json", StringComparison.Ordinal) && root.TryGetProperty("dependencies", out var frameworks))
                    foreach (var framework in frameworks.EnumerateObject()) foreach (var dependency in framework.Value.EnumerateObject())
                        items.Add(new(dependency.Name, dependency.Value.TryGetProperty("resolved", out var version) ? version.GetString() ?? "unknown" : "unknown", file.Path));
                else if (file.Path.EndsWith("package.json", StringComparison.Ordinal) && !files.Any(f => f.Path == file.Path[..^12] + "package-lock.json"))
                    foreach (var key in new[] { "dependencies", "devDependencies", "optionalDependencies" })
                        if (root.TryGetProperty(key, out var dependencies)) foreach (var dependency in dependencies.EnumerateObject()) items.Add(new(dependency.Name, dependency.Value.GetString() ?? "unknown", file.Path));
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { items.Add(new("invalid-manifest", "unknown", file.Path)); }
        }
        foreach (var project in files.Where(f => f.Path.EndsWith(".csproj", StringComparison.Ordinal) || f.Path.EndsWith(".props", StringComparison.Ordinal)))
            if (project.Content.Contains("PackageReference", StringComparison.Ordinal) && !items.Any(i => i.Path.EndsWith("packages.lock.json", StringComparison.Ordinal))) items.Add(new("unlocked-dotnet-dependencies", "unknown", project.Path));
        return items.Distinct().OrderBy(i => i.Name, StringComparer.Ordinal).ThenBy(i => i.Version, StringComparer.Ordinal).ThenBy(i => i.Path, StringComparer.Ordinal).ToArray();
    }
    private static DependencyEvidence? Evidence(SoftwareRun run, DependencyItem[] inventory)
    {
        var source = run.Seed.Sources.FirstOrDefault(s => s.SourceId == "dependency-audit-" + run.Seed.Project.ProjectId);
        if (source is null || !run.Seed.Memory.Any(m => m.Kind == "decision" && m.Citations.Any(c => c.SourceId == source.SourceId && c.Revision == source.Revision && c.ContentSha256 == source.Citation.ContentSha256))) return null;
        try
        {
            using var doc = JsonDocument.Parse(source.Text); var evidence = SoftwareJson.Parse<DependencyEvidence>(doc.RootElement);
            if (evidence.InventorySha256 != RuntimeJson.Hash(inventory) || evidence.GeneratedAt > DateTimeOffset.UtcNow.AddMinutes(5) || evidence.GeneratedAt < DateTimeOffset.UtcNow.AddDays(-30) ||
                string.IsNullOrWhiteSpace(evidence.Tool) || evidence.Tool.Length > 200 || string.IsNullOrWhiteSpace(evidence.ToolVersion) || evidence.ToolVersion.Length > 100 ||
                evidence.Findings is not { Length: <= 200 } || evidence.Findings.Any(f => f is null || !Identifiers.IsValid(f.Id) || f.Severity is not ("critical" or "high" or "medium" or "low" or "info") || f.Message is null || f.Message.Length > 2000) ||
                evidence.Licenses is not { Length: <= 5000 } || evidence.Licenses.Any(l => l is null || l.Name is not { Length: > 0 and <= 400 } || l.Version is not { Length: > 0 and <= 200 } || l.License is not { Length: > 0 and <= 120 } || l.Source is not { Length: > 0 and <= 2000 })) return null;
            return evidence;
        }
        catch (Exception e) when (e is JsonException or ArgumentException) { return null; }
    }
    public static ExecutionReceipt Security(SoftwareRun run, SoftwareJob job)
    {
        var started = DateTimeOffset.UtcNow; var findings = new List<RuntimeFinding>();
        foreach (var file in run.Candidate!.Files.Where(f => run.Candidate.ChangedPaths.Contains(f.Path)))
        {
            if (SourceSafety.ContainsKnownSecret(file.Content)) findings.Add(new("secret-" + findings.Count, "critical", "Known credential pattern found.", file.Path));
            if (DangerousCode().IsMatch(file.Content)) findings.Add(new("active-code-" + findings.Count, "high", "Dynamic evaluation/process execution requires a code change or independently scoped security review; it cannot auto-PASS.", file.Path));
        }
        var dependencies = Dependencies(run.Candidate.Files); var evidence = Evidence(run, dependencies);
        if (dependencies.Length > 0 && evidence is null) findings.Add(new("dependency-evidence", "high", "Fresh, inventory-matching vulnerability/license evidence must be imported and Owner-reviewed before creating this run."));
        if (evidence is not null) findings.AddRange(evidence.Findings);
        var stdout = JsonSerializer.Serialize(new { scanner = "aicos-static-rules/2.0.0", changedFiles = run.Candidate.ChangedPaths, dependencyInventorySha256 = RuntimeJson.Hash(dependencies), dependencyCount = dependencies.Length,
            advisoryEvidence = evidence is null ? "not_available" : "owner_reviewed_import", advisoryTool = evidence?.Tool, advisoryVersion = evidence?.ToolVersion,
            limitation = "Static source rules and imported evidence are not a penetration test or a complete vulnerability database." }, RuntimeJson.Options);
        return Receipt(run, job, "soc", started, findings, stdout);
    }
    public static ExecutionReceipt Quality(SoftwareRun run, SoftwareJob job)
    {
        var started = DateTimeOffset.UtcNow; var findings = new List<RuntimeFinding>();
        foreach (var criterion in run.Spec!.Acceptance)
            if (!run.Plan!.Tasks.Any(t => t.AcceptanceIds.Contains(criterion.Id))) findings.Add(new("coverage-" + criterion.Id, "high", "Acceptance criterion is not assigned to a task.", CriterionId: criterion.Id));
        foreach (var path in run.Candidate!.ChangedPaths)
            if (!run.Plan!.Tasks.Any(t => SoftwareContracts.Matches(path, t.AllowedPaths))) findings.Add(new("scope-" + findings.Count, "critical", "Change is outside all approved task scopes.", path));
        var dependencies = Dependencies(run.Candidate.Files); var evidence = Evidence(run, dependencies);
        foreach (var dependency in dependencies)
            if (evidence is null || !evidence.Licenses.Any(l => l.Name == dependency.Name && l.Version == dependency.Version && !string.IsNullOrWhiteSpace(l.License) && !new[] { "UNKNOWN", "NOASSERTION", "UNLICENSED" }.Contains(l.License.Trim(), StringComparer.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(l.Source)))
                findings.Add(new("license-" + findings.Count, "high", "License/provenance evidence is unknown for " + dependency.Name + " " + dependency.Version, dependency.Path));
        var stdout = JsonSerializer.Serialize(new { mappings = run.Spec.Acceptance.Select(c => new { c.Id, c.Description, c.Verification, taskIds = run.Plan!.Tasks.Where(t => t.AcceptanceIds.Contains(c.Id)).Select(t => t.Id),
            status = c.Verification == "manual" ? "owner_review_required" : "mapped_to_configured_test_suite" }), provenance = "Owner must review generated content/asset rights; no automatic legal clearance.",
            sourceFilesPreserved = run.Seed.Repository.Files.All(f => run.Candidate.Files.Any(c => c.Path == f.Path)), changedPaths = run.Candidate.ChangedPaths }, RuntimeJson.Options);
        return Receipt(run, job, "qa", started, findings, stdout);
    }
    private static ExecutionReceipt Receipt(SoftwareRun run, SoftwareJob job, string node, DateTimeOffset started, List<RuntimeFinding> findings, string stdout)
    {
        var items = findings.ToArray(); var failed = items.Any(f => f.Severity is "critical" or "high");
        return new("receipt-" + Guid.NewGuid().ToString("N"), job.JobId, job.RoleId, job.AgentId, "builtin." + node, "2.0.0", run.Candidate!.Sha256,
            "executed", failed ? 1 : 0, started, DateTimeOffset.UtcNow, stdout, "", LogHash(stdout, "", items, null, null), null, null, items,
            run.Plan!.Tasks.Any(t => t.AgentId == job.AgentId), Attempt: job.Attempts);
    }
    public static ExecutionReceipt NotRun(SoftwareRun run, string node, string reason)
    {
        var binding = run.Seed.Bindings.First(b => b.RoleId == node); var findings = new[] { new RuntimeFinding("not-run", "high", reason) };
        return new("receipt-" + Guid.NewGuid().ToString("N"), "not-run-" + Guid.NewGuid().ToString("N"), node, binding.Agent.AgentId, "docker." + run.Seed.Project.Test.Name, "1.0.0",
            run.Candidate?.Sha256 ?? run.Seed.Repository.Sha256, "not_run", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "", reason, LogHash("", reason, findings, null, run.Seed.Project.Test), null, run.Grant?.ImageId, findings, Command: run.Seed.Project.Test);
    }
    public static ExecutionReceipt NotRun(SoftwareRun run, SoftwareJob job, CommandSpec command, string reason)
    {
        var findings = new[] { new RuntimeFinding("sandbox-unavailable", "high", reason) };
        return new("receipt-" + Guid.NewGuid().ToString("N"), job.JobId, job.RoleId, job.AgentId, "docker." + command.Name, "1.0.0", run.Candidate!.Sha256,
            "not_run", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "", reason, LogHash("", reason, findings, null, command), null, run.Grant?.ImageId, findings, Command: command, Attempt: job.Attempts);
    }
}
