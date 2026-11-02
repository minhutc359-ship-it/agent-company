using System.IO.Compression;
using System.Net;
using System.Text.Json;
using AICompanyOS.Runtime;

public static class SetupTests
{
    private static int _checks;
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly LocalModel Model = new("demo-fixture:latest", "sha256:" + new string('b', 64), "protocol fixture", DateTimeOffset.UtcNow);
    private static void Check(bool value, string message) { _checks++; if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { _checks++; return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    public static async Task RunAsync()
    {
        foreach (var backend in new[] { "file", "sqlite" })
        {
            var root = Path.Combine(Path.GetTempPath(), "aicos-setup-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var store = new FileEventStore(root, backend); await store.InitializeAsync(Ct);
                var setup = new LocalSetupService(root); var repositories = new RepositoryService(root);
                await Reject<ArgumentException>(() => setup.InstallDemoAsync(store, repositories, "absent", [], Ct));
                var demo = await setup.InstallDemoAsync(store, repositories, null, [], Ct);
                var wb = await store.WorkbenchAsync(demo.CompanyId, Ct);
                Check(wb.Projects.Length == 1 && wb.Runs.Length == 0 && wb.AgentConfigurations.Length == 8 && !demo.ModelConfigured, "Demo setup creates real local source and a team without starting inference or claiming configured models.");
                var original = await repositories.ValidateRepositoryAsync(demo.RepositoryPath, Ct);
                Check(original.Length == 40 && File.ReadAllText(Path.Combine(demo.RepositoryPath, "tests", "validator.test.cjs")).Contains("[AC-001]"), "Demo contains a committed repository and a real intentionally failing acceptance test.");
                var note = Path.Combine(demo.RepositoryPath, "docs", "NOTES.md"); await File.WriteAllTextAsync(note, "Owner local edits must survive repeated setup.");
                await setup.InstallDemoAsync(store, repositories, Model.Name, [Model], Ct);
                Check(File.ReadAllText(note).StartsWith("Owner local edits") && (await store.WorkbenchAsync(demo.CompanyId, Ct)).Projects.Length == 1, "Repeated setup preserves existing source edits and project configuration.");
                // The starter deliberately preserves existing configurations; an explicit route change is required.
                Check(!(await setup.InstallDemoAsync(store, repositories, Model.Name, [Model], Ct)).ModelConfigured, "Selecting a model on a later install cannot silently overwrite existing manual routes.");
                await File.WriteAllTextAsync(note, "# Validation demo\nDocument how empty and valid input are handled.\n");
                var models = new FixtureModels(); var coordinator = new SoftwareCoordinator(store, repositories, new DockerSandbox(root, "unix:///tmp/aicos-absent-setup.sock"), models);
                var run = await coordinator.CreateAsync(demo.CompanyId, new(demo.ProjectId, demo.Objective), "handoff", Ct);
                Check(run.State == "waiting_input" && models.Calls == 0, "An unconfigured demo waits for a real typed result.");
                using (var zip = new ZipArchive(new MemoryStream(LocalSetupService.ExportHandoff(run, "ba"))))
                {
                    Check(zip.Entries.Select(e => e.FullName).Order().SequenceEqual(new[] { "REQUEST.md", "checkpoint.json", "context.json", "schema.json" }.Order()), "External handoff must contain the complete task, contract and pinned checkpoint.");
                    using var reader = new StreamReader(zip.GetEntry("context.json")!.Open());
                    Check(await reader.ReadToEndAsync() == run.Jobs.Single().Input && zip.Entries.All(e => e.Length > 0), "Export must contain actual immutable job bytes, never empty files or invented CLI execution.");
                }
                await Reject<WorkspaceConflictException>(() => Task.Run(() => LocalSetupService.ExportHandoff(run, "tester")));
                foreach (var id in new[] { "ba-agent", "leader-agent", "dev-agent" })
                {
                    var c = (await store.WorkbenchAsync(demo.CompanyId, Ct)).AgentConfigurations.Single(c => c.AgentId == id);
                    await store.ConfigureAgentAsync(demo.CompanyId, id, new(c.Version, c.Department, Model.Name, c.SkillRefs, 10000, 1024), Ct);
                }
                Check((await setup.InstallDemoAsync(store, repositories, Model.Name, [Model], Ct)).ModelConfigured, "Explicitly configured local routes are reflected accurately on a reopened demo.");
                await Discussion(store, demo.CompanyId);
                var manifest = await store.VerifyAsync(Ct); Check(manifest.Companies.Length > 0, "New demo and collaboration events retain journal integrity.");
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                    Directory.Delete(root, true);
                }
            }
        }
        await Catalog();
        Console.WriteLine($"PASS: {_checks} setup/demo/team assertions: real Git, preserved edits, complete CLI handoff, bounded pinned local turns, cancellation, lease fencing and credential-only model discovery.");
    }
    private static async Task Discussion(FileEventStore store, string company)
    {
        var request = new TeamDiscussionRequest("lobby", "main", "Coordinate a small change; keep the approved scope.", [new("ba-agent", "ba"), new("leader-agent", "leader"), new("dev-agent", "dev")]);
        await Reject<ArgumentException>(() => store.StartTeamDiscussionAsync(company, request with { Participants = [new("ba-agent", "ba")] }, [Model], "short", Ct));
        await Reject<ArgumentException>(() => store.StartTeamDiscussionAsync(company, request with { Participants = [new("ba-agent", "ba"), new("ba-agent", "ba")] }, [Model], "duplicate", Ct));
        await Reject<ArgumentException>(() => store.StartTeamDiscussionAsync(company, request with { Participants = [new("ba-agent", "ba"), new("foreign", "leader")] }, [Model], "foreign", Ct));
        var session = await store.StartTeamDiscussionAsync(company, request, [Model], "team-one", Ct);
        var count = (await store.ReadEventsAsync(company, 0, Ct)).Count;
        Check((await store.StartTeamDiscussionAsync(company, request, [Model], "team-one", Ct)).DiscussionId == session.DiscussionId && (await store.ReadEventsAsync(company, 0, Ct)).Count == count, "Discussion retries cannot duplicate the question or first model call.");
        await Reject<WorkspaceConflictException>(() => store.StartTeamDiscussionAsync(company, request with { Question = "changed" }, [Model], "team-one", Ct));
        Check(session.Participants.Length == 3 && session.MaxOutputTokens == 6144 && session.JobIds.Length == 1, "A discussion pins routes and budgets and enqueues only the first turn.");
        var config = (await store.WorkbenchAsync(company, Ct)).AgentConfigurations.Single(c => c.AgentId == "leader-agent");
        await store.ConfigureAgentAsync(company, "leader-agent", new(config.Version, config.Department, null, config.SkillRefs, 10000, 1024), Ct);
        string? prior = null;
        foreach (var id in new[] { "ba-agent", "leader-agent", "dev-agent" })
        {
            var job = await store.ClaimConsultationAsync("team-worker", Ct) ?? throw new InvalidOperationException("Missing discussion turn");
            Check(job.Binding.Agent.AgentId == id && job.Binding.Model?.Name == Model.Name && !job.Input.Contains("\"tools\":"), "Participant order and frozen local routes survive changes to the live configuration.");
            if (prior is not null) Check(job.Input.Contains(prior), "Later agents must receive the preceding agent's real public response.");
            prior = "Public response from " + id;
            await store.CompleteConsultationAsync(job, Reply(prior), null, Ct);
            await store.CompleteConsultationAsync(job, Reply("stale duplicate"), null, Ct);
        }
        var wb = await store.WorkbenchAsync(company, Ct);
        Check(wb.TeamDiscussions.Single().Status == "completed" && wb.TeamDiscussions.Single().JobIds.Length == 3 && wb.Interactions.Count(m => m.Origin == "model_proposal") == 3, "One turn per participant completes the session; stale completions cannot duplicate replies.");
        Check(await store.ClaimConsultationAsync("team-worker", Ct) is null, "The discussion terminates without an open-ended agent loop.");
        // Cancel an in-flight job, then reject both the stale completion and any retry.
        var shortRequest = request with { Participants = [new("ba-agent", "ba"), new("dev-agent", "dev")] };
        session = await store.StartTeamDiscussionAsync(company, shortRequest, [Model], "team-cancel", Ct);
        var running = (await store.ClaimConsultationAsync("team-worker", Ct))!;
        await store.CancelTeamDiscussionAsync(company, session.DiscussionId, Ct);
        await store.CompleteConsultationAsync(running, Reply("must not persist"), null, Ct);
        Check(!(await store.HeartbeatConsultationAsync(running, Ct)) && !(await store.WorkbenchAsync(company, Ct)).Interactions.Any(m => m.Summary == "must not persist"), "Cancellation fences an already running response and its heartbeat.");
        await Reject<WorkspaceConflictException>(() => store.RetryConsultationAsync(company, running.JobId, Ct));
        // A failed participant has one reviewed retry; cancelled failures cannot resume.
        session = await store.StartTeamDiscussionAsync(company, shortRequest, [Model], "team-fail", Ct);
        running = (await store.ClaimConsultationAsync("team-worker", Ct))!;
        await store.CompleteConsultationAsync(running, null, "fixture failure", Ct);
        await store.RetryConsultationAsync(company, running.JobId, Ct);
        running = (await store.ClaimConsultationAsync("team-worker", Ct))!;
        Check(running.Attempts == 2, "Discussion attempts are bounded by the existing consultation lease policy.");
        await store.CompleteConsultationAsync(running, null, "fixture failure", Ct);
        await Reject<WorkspaceConflictException>(() => store.RetryConsultationAsync(company, running.JobId, Ct));
        await store.CancelTeamDiscussionAsync(company, session.DiscussionId, Ct);
        // Simulate a process stopping after the reply transaction but before advancing its session.
        session = await store.StartTeamDiscussionAsync(company, shortRequest, [Model], "team-recover", Ct);
        running = (await store.ClaimConsultationAsync("old-team-worker", Ct))!;
        var publicReply = new PublicInteraction("recover-reply", "lobby", "answer", running.Binding.Agent.AgentId, ["owner"], "Persisted before process restart", [], [], running.ReplyTo, "recorded", "reply-" + running.JobId, DateTimeOffset.UtcNow, "model_proposal", "main", []);
        await store.AppendAsync(company, "lobby", [DomainEvent.Create("interaction.created", publicReply), DomainEvent.Create("consultation.changed", running with { Status = "completed", LeaseToken = null, LeaseUntil = null })], Ct);
        var recovered = (await store.ClaimConsultationAsync("replacement-team-worker", Ct))!;
        Check(recovered.Binding.Agent.AgentId == "dev-agent" && recovered.Input.Contains(publicReply.Summary), "Claim reconciliation advances an already saved response after a crash without repeating its model turn.");
        await store.CompleteConsultationAsync(recovered, Reply("Recovery complete"), null, Ct);
        Check((await store.WorkbenchAsync(company, Ct)).TeamDiscussions.Single(d => d.DiscussionId == session.DiscussionId).Status == "completed", "Recovered discussion reaches the same terminal state.");
        Check((await store.WorkbenchAsync("software-studio", Ct)).TeamDiscussions.Length == 0, "Discussion history cannot cross companies.");
    }
    private static SoftwareModelResult Reply(string summary)
    {
        var json = JsonSerializer.SerializeToElement(new PublicReply(summary, [], []), RuntimeJson.Options); return new(json, new(100, 40), RuntimeJson.Hash(json));
    }
    private static async Task Catalog()
    {
        foreach (var type in new[] { "openai", "gemini", "anthropic" })
        {
            var policy = CloudProviderPolicy.Resolve(type);
            var provider = new CloudProviderConfiguration("fixture", 1, type, "fixture-model", policy.Host, policy.Path, policy.Version, "memory", true, "fixture", DateTimeOffset.UtcNow);
            var catalog = new CloudModelCatalog(() => new Handler(request =>
            {
                Check(request.Method == HttpMethod.Get && request.Content is null && request.RequestUri!.Host == policy.Host && request.RequestUri.Scheme == "https", "Model discovery uses an allowlisted GET with no workspace data.");
                Check(type == "gemini" ? request.Headers.GetValues("x-goog-api-key").Single() == "credential" : request.Headers.Authorization?.Parameter == "credential", "Each model-list endpoint uses its own authentication contract.");
                return new(HttpStatusCode.OK) { Content = new StringContent(type == "gemini" ? "{\"models\":[{\"name\":\"models/fixture-model\"}]}" : "{\"data\":[{\"id\":\"fixture-model\"}]}") };
            }));
            var result = await catalog.CheckAsync(provider, "credential", Ct);
            Check(result.Available && result.ConfiguredModelListed && result.Models.Single() == provider.ModelId && !JsonSerializer.Serialize(result).Contains("credential"), "Catalog results expose model names, not credentials or generation claims.");
            var rejected = await new CloudModelCatalog(() => new Handler(_ => new(HttpStatusCode.Unauthorized))).CheckAsync(provider, "credential", Ct);
            Check(!rejected.Available && rejected.Models.Length == 0, "Failed authentication is never reported as ready.");
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request)); }
    private sealed class FixtureModels : ISoftwareModelClient
    {
        public int Calls;
        public Task<LocalModel[]> ListAsync(CancellationToken ct) => Task.FromResult(new[] { Model });
        public Task<SoftwareModelResult> GenerateAsync(RoleBinding binding, SoftwareJob job, CancellationToken ct) { Calls++; throw new InvalidOperationException("No model generation is authorized during setup."); }
    }
}
