using System.Text;
using System.Text.Json;
using AICompanyOS.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

public static class SoftwareTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static int _checks;
    private const string Company = "software-studio";
    private static readonly LocalModel Model = new("fixture:latest", "sha256:" + new string('a', 64), "protocol fixture", DateTimeOffset.UtcNow);
    private static void Check(bool value, string message) { _checks++; if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task> fn, string message) where T : Exception { try { await fn(); } catch (T) { _checks++; return; } throw new InvalidOperationException(message); }
    private static string Temp(string label) { var p = Path.Combine(Path.GetTempPath(), "aicos-software-" + label + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
    private static void DeleteFixture(string root)
    {
        if (!Directory.Exists(root)) return;
        // Git for Windows marks loose objects readonly. This is confined to this test's temporary fixture.
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(root, true);
    }
    private static CommandSpec Build => new("build", "node", ["--check", "src/validator.cjs"], 30);
    private static CommandSpec Test => new("test", "node", ["--test", "tests/validator.test.cjs"], 30);
    private static RequirementSpec Spec => new("Non-empty strings", ["Validate a nonempty string without changing unrelated code."], [new("AC-001", "Reject empty values; accept non-empty strings.", "test")], [], []);
    private static TaskGraph Plan => new("Independent implementation and documentation tasks.", [new("code", "Implement non-empty validation", "dev-agent", [], ["src/"], ["AC-001"]), new("docs", "Document validation behavior", "dev-2", [], ["docs/"], ["AC-001"])]);
    private static async Task<string> FixtureRepo(string root)
    {
        var repo = Path.Combine(root, "repo"); Directory.CreateDirectory(Path.Combine(repo, "src")); Directory.CreateDirectory(Path.Combine(repo, "tests")); Directory.CreateDirectory(Path.Combine(repo, "docs"));
        await File.WriteAllTextAsync(Path.Combine(repo, "src", "validator.cjs"), "exports.validate = value => value != null;\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "tests", "validator.test.cjs"), "const test = require('node:test'); const assert = require('node:assert/strict'); const { validate } = require('../src/validator.cjs'); test('[AC-001] non-empty validation', () => { assert.equal(validate(''), false); assert.equal(validate('valid'), true); });\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "docs", "NOTES.md"), "Original notes.\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "package.json"), "{\"name\":\"aicos-fixture\",\"private\":true,\"version\":\"1.0.0\"}\n");
        var process = new TrustedProcess(Path.Combine(root, "git-home"));
        async Task Git(params string[] args) { var r = await process.RunAsync("git", ["-C", repo, .. args], root, 15, Ct); if (r.ExitCode != 0) throw new InvalidOperationException(r.Stderr); }
        await Git("init"); await Git("config", "user.name", "Fixture"); await Git("config", "user.email", "fixture@localhost"); await Git("add", "."); await Git("commit", "-m", "Fixture baseline"); return repo;
    }
    public static async Task RunAsync()
    {
        Compiler();
        Check(CompanyCommandParser.Resolve("/skill:spec").NodeId == "ba" && CompanyCommandParser.Resolve("/pause").Mutating, "Slash commands resolve explicit role/skill/runtime namespaces.");
        await Throws<ArgumentException>(() => Task.Run(() => CompanyCommandParser.Resolve("/test ; rm -rf x")), "Slash commands must never become shell commands.");
        foreach (var backend in new[] { "file", "sqlite" }) await DurableManualAsync(backend);
        LargeBackup();
        await ActualSandboxAsync();
        Console.WriteLine($"PASS: {_checks} software/company assertions: scoped worktrees, compiler, typed artifacts, grants, fail-closed execution, collaboration, storage parity and durable policy pins.");
    }
    private static void Compiler()
    {
        var definition = SoftwareContracts.DefaultWorkflow;
        Check(SoftwareContracts.Compile(definition).Valid, "Built-in DAG must validate.");
        Check(SoftwareContracts.Compile(definition).ExpandedNodeIds.Contains("gate:round-2"), "Bounded gate loops must expand into finite attempts.");
        Check(!SoftwareContracts.Compile(definition with { Nodes = definition.Nodes.Select(n => n.Id == "ba" ? n with { DependsOn = ["leader"] } : n).ToArray() }).Valid, "Ordinary dependency cycles must be rejected.");
        Check(!SoftwareContracts.Compile(definition with { Nodes = definition.Nodes.Where(n => n.Id != "soc").ToArray() }).Valid, "Mandatory SOC cannot be removed.");
        Check(!SoftwareContracts.Compile(definition with { Nodes = definition.Nodes.Select(n => n.Id == "subpm" ? n with { RunOnFailure = false } : n).ToArray() }).Valid, "SubPM must report failure paths.");
        Check(!SoftwareContracts.Compile(definition with { Nodes = definition.Nodes.Select(n => n.Id == "gate" ? n with { MaxLoop = 99 } : n).ToArray() }).Valid, "Unbounded retry loops cannot compile.");
        foreach (var path in new[] { "../secrets", "/etc/passwd", ".git/config", "src/../x", "src/.env", "C:/test", "src\\main.js", "CON.txt" }) Check(!SoftwareContracts.SafeRelative(path), "Unsafe path must be rejected: " + path);
        Check(!SoftwareContracts.Matches("secrets/key.txt", ["*"]), "Wildcard scope cannot grant secret paths.");
        Check(!SoftwareContracts.ScopeSubset("*", ["src/"]), "Task scopes cannot expand project grants.");
        Check(!SoftwareContracts.Compile(definition with { Nodes = [.. definition.Nodes, new("unknown-agent", "agent", "ba", ["ba"])] }).Valid, "Unexecutable additional node schemas must not appear validated.");
        Check(RepositoryService.TouchesOmitted("src/binary/file.txt", ["src/BINARY"]) && RepositoryService.TouchesOmitted("src/Binary", ["src/binary/child.txt"]), "Omitted path collisions are denied case-insensitively in both file/directory directions.");
        Check(!SoftwareContracts.SafeRelative("src/COM9.txt"), "Windows reserved names cannot enter cross-platform snapshots.");
        var dependencies = SoftwareChecks.Dependencies([new("package.json", "{\"dependencies\":{\"example\":\"^1.0.0\"}}", ""), new("package-lock.json", "{\"packages\":{\"node_modules/example\":{\"version\":\"1.0.1\"}}}", "")]);
        Check(dependencies.Length == 1 && dependencies[0].Version == "1.0.1", "Dependency inventory prefers resolved lock versions to semver ranges.");
    }
    private static async Task DurableManualAsync(string backend)
    {
        var root = Temp(backend); var data = Path.Combine(root, "data"); var restoredPath = Path.Combine(root, "restored");
        try
        {
            var repo = await FixtureRepo(root); var repositories = new RepositoryService(data); var models = new FixtureModels();
            // An intentionally absent local socket verifies fail-closed behavior even on hosts that do have Docker.
            var sandbox = new DockerSandbox(data, "unix:///tmp/aicos-intentionally-absent.sock");
            byte[] backup; string runId;
            using (var store = new FileEventStore(data, backend))
            {
                await store.InitializeAsync(Ct); await store.CreateAgentAsync(Company, new("dev-2", "Second developer", ["dev"]), Ct);
                var project = await store.ConfigureProjectAsync(Company, new("fixture", "Fixture project", repo, ["*"], ["src/", "docs/", "tests/"], "node:22-alpine", Build, Test), repositories, Ct);
                var snapshot = await repositories.SnapshotAsync(project, Ct);
                Check(snapshot.Files.Length == 4 && snapshot.BaseCommit.Length == 40, "Repository intake reads exact safe committed source files.");
                await Throws<ArgumentException>(() => store.ConfigureProjectAsync(Company, new("unsafe", "Unsafe", repo, ["src/"], ["*"], "node:22-alpine", Build, Test), repositories, Ct), "A project cannot expand writes beyond readable scope.");
                var skill = new SkillPackage("safe-code", "1.0.0", "Preserve public contracts", "Keep behavior outside the assigned acceptance unchanged.", ["dev"], ["repo.read"]);
                await store.InstallSkillAsync(Company, skill, Ct);
                await Throws<WorkspaceConflictException>(() => store.InstallSkillAsync(Company, skill with { Instructions = "changed" }, Ct), "Skill versions must remain immutable.");
                await store.ConfigureAgentAsync(Company, "dev-agent", new(0, "Engineering", null, ["safe-code@1.0.0"]), Ct);
                var coordinator = new SoftwareCoordinator(store, repositories, sandbox, models);
                var request = new SoftwareCreateRequest(project.ProjectId, "Implement non-empty validation and document it", ReleaseRequested: true);
                var run = await coordinator.CreateAsync(Company, request, "manual-create", Ct); runId = run.Seed.RunId;
                Check(run.State == "waiting_input" && models.Calls == 0, "No model route means typed Owner input; never fake inference.");
                var repeated = await coordinator.CreateAsync(Company, request, "manual-create", Ct);
                Check(repeated.Seed.RunId == runId, "Software intake must be idempotent.");
                run = (await store.ControlSoftwareAsync(Company, runId, "pause", run.LastSequence, "pause-intake", Ct)).Run!;
                Check(run.State == "paused", "Owner can pause typed-input nodes.");
                run = (await store.ControlSoftwareAsync(Company, runId, "step", run.LastSequence, "step-ba", Ct)).Run!;
                Check(run.StepMode, "Step persists a single-node execution mode.");
                run = await coordinator.SupplyOutputAsync(Company, runId, "ba", new(run.LastSequence, JsonSerializer.SerializeToElement(Spec, RuntimeJson.Options)), Ct);
                Check(run.State == "paused" && run.Spec is not null && !run.Jobs.Any(j => j.NodeId == "leader"), "One step stops before dispatching the next node.");
                run = (await store.ControlSoftwareAsync(Company, runId, "resume", run.LastSequence, "resume-leader", Ct)).Run!;
                Check(run.HandoffAcks!.All(a => run.Handoffs!.Any(p => p.ContentSha256 == a.ContentSha256 && p.ToAgentId == a.ReceiverAgentId && p.InputSha256 == a.InputSha256)), "Each invocation ACK binds its exact receiver and immutable context hash.");
                Check(run.Spec!.Acceptance.Single().Id == "AC-001" && run.Jobs.Any(j => j.NodeId == "leader" && j.Status == "waiting_input"), "Validated requirements must feed the Leader node.");
                run = await coordinator.SupplyOutputAsync(Company, runId, "leader", new(run.LastSequence, JsonSerializer.SerializeToElement(Plan, RuntimeJson.Options)), Ct);
                Check(run.State == "awaiting_plan_approval" && !run.Jobs.Any(j => j.TaskId is not null), "Plan output must not silently grant worktree/container access.");
                var revisionRequest = new SoftwareRevisionRequest(run.LastSequence, "plan", "Keep implementation and docs in independent task scopes.");
                run = (await store.RequestSoftwareRevisionAsync(Company, runId, revisionRequest, "plan-revision", Ct)).Run!;
                Check(run.Plan is null && run.Grant is null && run.OwnerFeedback!.Single().Stage == "plan", "Plan revision invalidates prior plan and grant while retaining explicit feedback.");
                Check((await store.RequestSoftwareRevisionAsync(Company, runId, revisionRequest, "plan-revision", Ct)).Outcome == CommandOutcome.AlreadyApplied, "Owner feedback commands are idempotent.");
                Check(run.Jobs.Last(j => j.NodeId == "leader").Input.Contains("independent task scopes"), "Revised Leader context includes Owner feedback.");
                run = await coordinator.SupplyOutputAsync(Company, runId, "leader", new(run.LastSequence, JsonSerializer.SerializeToElement(Plan, RuntimeJson.Options)), Ct);
                await Throws<ExecutionBlockedException>(() => coordinator.PreviewAsync(Company, runId, run.LastSequence, Ct), "Unavailable sandbox must block executable approval preview.");
                // Domain-only grant fixture: production HTTP can obtain this only after a real sandbox preflight.
                run = await store.PreviewExecutionGrantAsync(Company, runId, run.LastSequence, "sha256:" + new string('b', 64), Ct);
                Check((await store.ApproveSoftwarePlanAsync(Company, runId, new(run.LastSequence, true, "wrong"), "bad-grant", Ct)).Outcome == CommandOutcome.Conflict, "Wrong execution-manifest hashes must be rejected.");
                run = (await store.ApproveSoftwarePlanAsync(Company, runId, new(run.LastSequence, true, run.Grant!.ManifestSha256), "grant", Ct)).Run!;
                Check(run.Jobs.Count(j => j.TaskId is not null && j.Status == "waiting_input") == 2, "Independent Dev tasks must fan out separately.");
                Check(run.Seed.Bindings.Single(b => b.RoleId == "dev" && b.Agent.AgentId == "dev-agent").Skills.Single().Version == "1.0.0", "Run pins exact skill versions.");
                await store.ConfigureAgentAsync(Company, "dev-agent", new(1, "Engineering", null, []), Ct);
                Check(run.Seed.Bindings.Single(b => b.RoleId == "dev" && b.Agent.AgentId == "dev-agent").Skills.Length == 1, "Later agent edits cannot mutate pinned run instructions.");
                foreach (var task in Plan.Tasks)
                {
                    run = (await store.GetSoftwareRunAsync(Company, runId, Ct))!;
                    var source = SoftwareContext.TaskBaseline(run, task.Id).Single(f => f.Path == (task.Id == "code" ? "src/validator.cjs" : "docs/NOTES.md"));
                    var patch = new RepositoryPatch(task.Title, [new(source.Path, source.Sha256, task.Id == "code" ? "exports.validate = value => typeof value === 'string' && value.trim().length > 0;\n" : "Validation rejects empty strings.\n")]);
                    run = await coordinator.SupplyOutputAsync(Company, runId, "dev-" + task.Id, new(run.LastSequence, JsonSerializer.SerializeToElement(patch, RuntimeJson.Options)), Ct);
                }
                Check(run.Candidate?.ChangedPaths.Length == 2 && run.Patches.All(p => Directory.Exists(p.Worktree)), "Dev produces real isolated worktrees and an integrated candidate.");
                Check((await File.ReadAllTextAsync(Path.Combine(repo, "src", "validator.cjs"))).Contains("value != null"), "The source working directory must remain unchanged.");
                var worker = new SoftwareWorker(store, coordinator, sandbox, models, NullLogger<SoftwareWorker>.Instance);
                for (var i = 0; i < 10 && await worker.RunOneAsync(Ct); i++) { }
                run = (await store.GetSoftwareRunAsync(Company, runId, Ct))!;
                Check(run.State == "blocked" && run.Round == 0, "Infrastructure failures escalate instead of wasting corrective Dev loops.");
                Check(run.Receipts.Any(r => r.Tool == "docker.build" && r.Status == "not_run" && r.ExitCode is null), "Missing Docker must yield a not-run receipt, never a passing command.");
                Check(run.Nodes.Single(n => n.Id == "subpm").Status == "passed" && run.Report.Status == "blocked", "SubPM must produce an evidence-backed failure report.");
                Check((await store.ListProposalsAsync(Company, Ct)).Any(p => p.Status == "proposed" && p.Citations.Any(c => c.SourceId == "report-" + runId)) && (await store.ListCanonicalAsync(Company, Ct)).Length == 0, "SubPM proposes source-cited memory without auto-accepting an execution claim.");
                Check(!SoftwareChecks.GatesPass(run), "No host-command fallback can satisfy mandatory build/test gates.");
                Check(run.Patches.All(p => run.Report.ReceiptIds.Contains(p.Receipt!.ReceiptId)), "Report includes real Dev worktree receipts as well as gate receipts.");
                var manyFiles = Enumerable.Range(0, 150).Select(i => new RepositoryFile($"docs/context-{i}.txt", "Read complete files before writing.\n", RuntimeJson.TextHash("Read complete files before writing.\n"))).ToArray();
                var broadRun = run with { Seed = run.Seed with { Repository = run.Seed.Repository with { Files = [.. run.Seed.Repository.Files, .. manyFiles] } } };
                var boundedContext = SoftwareContext.Build(broadRun, "ba", null, "ba");
                Check(boundedContext.Contains("inventory is partial") && boundedContext.Contains("safeFileCount"), "Large repositories use an explicit partial inventory rather than dropping mandatory constraints or silently truncating source files.");
                var recoveryJob = new SoftwareJob("partial-worktree", runId, Company, "dev-code", "dev", "dev-agent", run.Round, "code", "", "", "failed");
                var directory = await repositories.MaterializeWorktreeAsync(run, recoveryJob, run.Seed.Repository.Files, Ct);
                File.Delete(directory + ".json");
                await Throws<WorkspaceConflictException>(() => repositories.MaterializeWorktreeAsync(run, recoveryJob, run.Seed.Repository.Files, Ct), "Incomplete worktrees cannot be reused without explicit reconciliation.");
                await repositories.ReconcileWorktreeAsync(run, recoveryJob, Ct);
                Check(!Directory.Exists(directory) && (await repositories.ValidateRepositoryAsync(repo, Ct)) == run.Seed.Repository.BaseCommit, "Reconciliation removes only the registered failed worktree and preserves the source checkout.");
                var auditFiles = run.Candidate!.Files.Where(f => f.Path != "package.json").Append(new RepositoryFile("package.json", "{\"dependencies\":{\"example\":\"1.0.0\"}}", "fixture")).ToArray();
                var inventory = SoftwareChecks.Dependencies(auditFiles);
                var evidence = new DependencyEvidence(RuntimeJson.Hash(inventory), DateTimeOffset.UtcNow, "reviewed-audit-fixture", "1.0", [], [new("example", "1.0.0", "unknown", "fixture source")]);
                var auditText = JsonSerializer.Serialize(evidence, RuntimeJson.Options); var auditSource = new SourceExcerpt("dependency-audit-fixture", 1, "Audit", auditText, new("dependency-audit-fixture", 1, 0, auditText.Length, RuntimeJson.TextHash(auditText)));
                var auditRun = run with { Candidate = run.Candidate with { Files = auditFiles }, Seed = run.Seed with { Sources = [auditSource], Memory = [new("audit-reviewed", "decision", "Reviewed imported dependency evidence", [auditSource.Citation], 1, "local-owner", "fixture")] } };
                var qaJob = new SoftwareJob("audit-qa", runId, Company, "qa", "qa", "qa-agent", 0, null, "", "", Attempts: 1);
                Check(SoftwareChecks.Quality(auditRun, qaJob).Findings.Any(f => f.Id.StartsWith("license-")), "Case variants of unknown license evidence still block QA.");
                evidence = evidence with { Licenses = [new("example", "1.0.0", "MIT", "reviewed fixture provenance")] };
                auditText = JsonSerializer.Serialize(evidence, RuntimeJson.Options); auditSource = auditSource with { Text = auditText, Citation = auditSource.Citation with { End = auditText.Length, ContentSha256 = RuntimeJson.TextHash(auditText) } };
                auditRun = auditRun with { Seed = auditRun.Seed with { Sources = [auditSource], Memory = [new("audit-reviewed", "decision", "Reviewed imported evidence", [auditSource.Citation], 1, "local-owner", "fixture")] } };
                Check(!SoftwareChecks.Quality(auditRun, qaJob).Findings.Any(f => f.Id.StartsWith("license-")), "Fresh exact inventory-matching reviewed provenance can satisfy the bounded license evidence check.");
                var recovery = await coordinator.CreateAsync(Company, request, "recovery-create", Ct);
                var oldClaim = await store.ClaimManualSoftwareOutputAsync(Company, recovery.Seed.RunId, "ba", recovery.LastSequence, Ct);
                await store.RecoverSoftwareJobsAsync("replacement-worker", Ct);
                Check(!await store.CompleteSoftwareJobAsync(oldClaim, Spec, null, null, Ct), "Restart fences a stale worker result rather than double-applying it.");
                recovery = (await store.GetSoftwareRunAsync(Company, recovery.Seed.RunId, Ct))!;
                Check(recovery.Jobs.Single().Status == "interrupted", "Interrupted jobs require an explicit reviewed retry.");
                recovery = (await store.ControlSoftwareAsync(Company, recovery.Seed.RunId, "retry", recovery.LastSequence, "recover-retry", Ct)).Run!;
                Check(recovery.Jobs.Single().Status == "waiting_input", "Owner-supplied jobs recover as typed input rather than invented inference.");
                await store.ControlSoftwareAsync(Company, recovery.Seed.RunId, "cancel", recovery.LastSequence, "recover-cancel", Ct);
                var legacy = (await store.CreateRunAsync(Company, "recent-static-fixture", "Preserve Office projection for the static flow", [], "recent-static", Ct)).Run;
                var mixedOffice = await store.OfficeSnapshotAsync(Company, Ct);
                Check(mixedOffice.Agents.Any(a => a.RunId == legacy.RunId && a.State == "handoff_pending"), "A newer active static workflow remains visible after software runs exist.");
                await store.CreateCompanyAsync(new("other", "Other company"), Ct);
                Check(await store.GetSoftwareRunAsync("other", runId, Ct) is null, "Software state is company-scoped.");
                var question = await store.AddInteractionAsync(Company, new("lobby", "consult", "owner", ["ba-agent"], "Review local constraints.", [], []), "message", Ct);
                Check(question.Origin == "owner_authored", "Owner notes must be labelled distinctly from model replies.");
                var replay = await store.AddInteractionAsync(Company, new("lobby", "consult", "owner", ["ba-agent"], "Review local constraints.", [], []), "message", Ct);
                Check(question.InteractionId == replay.InteractionId, "Public interaction retry must not duplicate a message.");
                await Throws<ArgumentException>(() => store.AddInteractionAsync(Company, new("lobby", "consult", "owner", ["foreign-agent"], "x", [], []), "foreign", Ct), "Foreign participants cannot leak context across companies.");
                var meeting = await store.CreateMeetingAsync(Company, new("lobby", "Discuss accepted constraints", ["ba-agent", "leader-agent"]), "meeting", Ct);
                meeting = await store.ChangeMeetingAsync(Company, meeting.MeetingId, true, new(meeting.Version, "", [], []), Ct);
                var office = await store.OfficeSnapshotAsync(Company, Ct);
                Check(office.Agents.Where(a => meeting.Participants.Contains(a.Agent.AgentId)).All(a => a.MeetingId == meeting.MeetingId), "Office meetings must project persisted meeting records.");
                meeting = await store.ChangeMeetingAsync(Company, meeting.MeetingId, false, new(meeting.Version, "Preserve local data", ["Review before release"], []), Ct);
                Check((await store.GetSourceAsync(Company, meeting.MeetingId, 1, Ct))!.Content.Contains("Preserve local data"), "Completed meeting minutes must exist as a versioned source artifact.");
                var manifest = await store.VerifyAsync(Ct); await store.MigrateAsync(backend == "file" ? "sqlite" : "file", manifest.Digest, Ct);
                Check((await store.GetSoftwareRunAsync(Company, runId, Ct))!.Candidate!.Sha256 == run.Candidate!.Sha256, "Software artifacts and links survive backend migration.");
                backup = await store.ExportBackupAsync("software-test-backup-passphrase", Ct);
            }
            using (var reopened = new FileEventStore(data))
            {
                await reopened.InitializeAsync(Ct); var run = (await reopened.GetSoftwareRunAsync(Company, runId, Ct))!;
                Check(run.State == "blocked" && run.Receipts.Length >= 4 && run.Patches.Length == 2, "Restart must retain exact patch/gate evidence without inventing command execution.");
            }
            using (var restored = new FileEventStore(restoredPath))
            {
                await restored.RestoreIntoEmptyAsync(backup, "software-test-backup-passphrase", Ct);
                Check((await restored.WorkbenchAsync(Company, Ct)).Meetings.Single().State == "completed", "Encrypted restore preserves company collaboration and project/run relationships.");
            }
        }
        finally { DeleteFixture(root); }
    }
    private static void LargeBackup()
    {
        var e = DomainEvent.Create("large.backup.fixture", new { content = new string('x', 9 * 1024 * 1024) }) with { Seq = 1, RunId = "large-fixture", CompanyId = Company };
        e = e with { ContentSha256 = RuntimeJson.Hash(new { e.Seq, e.EventId, e.RunId, e.Type, e.CreatedAt, e.Data, e.CompanyId, e.PrevHash }) };
        var companies = new Dictionary<string, DomainEvent[]> { [Company] = [e] };
        var backup = BackupCodec.Encrypt(new(1, DateTimeOffset.UtcNow, companies, BackupCodec.Digest(companies)), "large-backup-test-passphrase");
        Check(BackupCodec.Decrypt(backup, "large-backup-test-passphrase").Companies[Company].Single().ContentSha256 == e.ContentSha256, "A snapshot larger than the old 8 MiB plaintext ceiling must authenticate and preserve its event hash.");
    }
    private static async Task ActualSandboxAsync()
    {
        var root = Temp("docker");
        try
        {
            var data = Path.Combine(root, "data"); var repo = await FixtureRepo(root); var repositories = new RepositoryService(data); var sandbox = new DockerSandbox(data); var models = new FixtureModels();
            var capability = await sandbox.ProbeAsync(Environment.GetEnvironmentVariable("AICOS_TEST_IMAGE") ?? "node:22-alpine", Ct);
            if (!capability.Available)
            {
                if (Environment.GetEnvironmentVariable("AICOS_REQUIRE_SANDBOX") == "1") throw new InvalidOperationException("Required actual Docker integration is unavailable: " + capability.Reason);
                Console.WriteLine("NOT RUN: actual Docker integration requires a local Linux Docker engine and a preinstalled node:22-alpine image. No host execution substitute was used."); return;
            }
            using var store = new FileEventStore(data); await store.InitializeAsync(Ct); await store.CreateAgentAsync(Company, new("dev-2", "Second developer", ["dev"]), Ct);
            await store.ConfigureProjectAsync(Company, new("fixture", "Docker fixture", repo, ["*"], ["src/", "docs/", "tests/"], Environment.GetEnvironmentVariable("AICOS_TEST_IMAGE") ?? "node:22-alpine", Build, Test), repositories, Ct);
            foreach (var id in new[] { "ba-agent", "leader-agent", "dev-agent", "dev-2" }) await store.ConfigureAgentAsync(Company, id, new(0, "Engineering", Model.Name, []), Ct);
            var coordinator = new SoftwareCoordinator(store, repositories, sandbox, models); var worker = new SoftwareWorker(store, coordinator, sandbox, models, NullLogger<SoftwareWorker>.Instance);
            var run = await coordinator.CreateAsync(Company, new("fixture", "Fix empty string validation, document it and run actual tests", ReleaseRequested: true), "docker-create", Ct);
            await worker.RunOneAsync(Ct); await worker.RunOneAsync(Ct); run = (await store.GetSoftwareRunAsync(Company, run.Seed.RunId, Ct))!;
            Check(run.State == "awaiting_plan_approval", "Model-backed BA/Leader must reach the explicit execution gate.");
            run = await coordinator.PreviewAsync(Company, run.Seed.RunId, run.LastSequence, Ct);
            run = (await store.ApproveSoftwarePlanAsync(Company, run.Seed.RunId, new(run.LastSequence, true, run.Grant!.ManifestSha256), "real-grant", Ct)).Run!;
            for (var i = 0; i < 30; i++)
            {
                await Task.WhenAll(worker.RunOneAsync(Ct), worker.RunOneAsync(Ct));
                run = (await store.GetSoftwareRunAsync(Company, run.Seed.RunId, Ct))!;
                if (run.State == "awaiting_final_approval" || run.State == "blocked" && !run.Jobs.Any(j => j.Status is "queued" or "running")) break;
            }
            Check(run.State == "awaiting_final_approval", "Full actual sandbox flow did not pass: " + JsonSerializer.Serialize(new { run.State, run.Round, run.Report, run.Nodes }));
            Check(run.Round == 1 && run.Receipts.Any(r => r.Tool == "docker.test" && r.Status == "failed"), "An actual failing test must trigger a bounded corrective Dev round.");
            Check(SoftwareChecks.GatesPass(run) && SoftwareChecks.CurrentReceipts(run).Any(r => r.Tool == "docker.test" && r.ExitCode == 0 && r.ImageId == capability.ImageId), "Final PASS requires actual Docker execution on the pinned image and candidate.");
            Check(run.Patches.Select(p => p.Worktree).Distinct().Count() == 4 && File.ReadAllText(Path.Combine(repo, "src", "validator.cjs")).Contains("value != null"), "Parallel Dev attempts must use separate worktrees without editing source HEAD.");
            run = (await store.ReviewSoftwareDeliveryAsync(Company, run.Seed.RunId, new(run.LastSequence, run.Candidate!.Sha256, true, true), "accept", Ct)).Run!;
            var preview = SoftwareReleaseService.Preview(run, false);
            Check((await store.RequestSoftwareRevisionAsync(Company, run.Seed.RunId, new(run.LastSequence, "delivery", "late edit"), "late-revision", Ct)).Outcome == CommandOutcome.Conflict, "Accepted delivery cannot be silently revised during release.");
            var release = new SoftwareReleaseService(store, repositories, data);
            run = await release.ExecuteAsync(Company, run.Seed.RunId, new(run.LastSequence, run.Candidate!.Sha256, true, true, TargetManifestSha256: preview.ManifestSha256), "commit", Ct);
            Check(run.State == "completed" && run.Release!.CommitSha.Length == 40 && !run.Release.Pushed, "Final scoped commit must be real and must not implicitly push.");
            var content = await repositories.GitAsync(repo, ["show", run.Release!.CommitSha + ":src/validator.cjs"], Ct);
            Check(content.Stdout.Contains("trim().length > 0"), "Committed source must match the corrected, tested candidate bytes.");
            Console.WriteLine("PASS: actual Docker software flow: parallel Dev worktrees, failing test → corrective round → passing build/test/SOC/QA → Owner → scoped Git commit; network none, readonly root and no host mounts.");
        }
        finally { DeleteFixture(root); }
    }
    private sealed class FixtureModels : ISoftwareModelClient
    {
        public int Calls;
        public Task<LocalModel[]> ListAsync(CancellationToken ct) => Task.FromResult(new[] { Model });
        public Task<SoftwareModelResult> GenerateAsync(RoleBinding binding, SoftwareJob job, CancellationToken ct)
        {
            Calls++; object result;
            if (job.NodeId == "ba") result = Spec;
            else if (job.NodeId == "leader") result = Plan;
            else
            {
                using var payload = JsonDocument.Parse(job.Input); using var context = JsonDocument.Parse(payload.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
                var path = job.TaskId == "code" ? "src/validator.cjs" : "docs/NOTES.md";
                var file = context.RootElement.GetProperty("repositoryFiles").EnumerateArray().Single(f => f.GetProperty("path").GetString() == path);
                result = new RepositoryPatch("Fixture patch for " + job.TaskId, [new(path, file.GetProperty("sha256").GetString(), job.TaskId == "code" ? job.Round == 0 ? "// first candidate intentionally fails an actual assertion\nexports.validate = value => value != null;\n" : "exports.validate = value => typeof value === 'string' && value.trim().length > 0;\n" : "Validation rejects empty strings.\n")]);
            }
            var output = JsonSerializer.SerializeToElement(result, RuntimeJson.Options);
            return Task.FromResult(new SoftwareModelResult(output, new(120, 80), RuntimeJson.Hash(output)));
        }
    }
}
