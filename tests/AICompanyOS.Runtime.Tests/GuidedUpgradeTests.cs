using System.Security.Cryptography;
using System.Text.Json;
using AICompanyOS.Runtime;
using Microsoft.Extensions.Hosting;

public static class GuidedUpgradeTests
{
    private static int _checks;
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static void Check(bool value, string message) { _checks++; if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { _checks++; return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    public static async Task RunAsync()
    {
        foreach (var backend in new[] { "file", "sqlite" })
        {
            var root = Path.Combine(Path.GetTempPath(), "aicos-guided-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                using var store = new FileEventStore(root, backend); await store.InitializeAsync(Ct);
                var repositories = new RepositoryService(root); var demo = await new LocalSetupService(root).InstallDemoAsync(store, repositories, null, [], Ct); var company = demo.CompanyId;
                var wb = await store.WorkbenchAsync(company, Ct); var profile = wb.Profile;
                await store.ConfigureCompanyAsync(company, new(profile.Version, profile.Description, profile.Departments, false, "controlled_cloud"), Ct);
                var provider = await store.ConfigureCloudProviderAsync(company, "claude-fixture", new(0, "anthropic", "fixture-model"), Ct);
                var config = wb.AgentConfigurations.Single(c => c.AgentId == "ba-agent");
                await store.ConfigureAgentAsync(company, config.AgentId, new(config.Version, config.Department, provider.ModelId, config.SkillRefs, 10000, 1024, provider.ProviderId), Ct);
                var local = new NoLocalCalls(); var coordinator = new SoftwareCoordinator(store, repositories, new DockerSandbox(root, "unix:///tmp/aicos-guided-no-socket.sock"), local);
                var run = await coordinator.CreateAsync(company, new(demo.ProjectId, demo.Objective), "cloud-run", Ct);
                Check(run.State == "waiting_cloud_approval" && local.ListCalls == 0 && run.Seed.Bindings.Single(b => b.RoleId == "ba").Cloud?.Provider.Version == provider.Version, "Cloud software bindings pin their provider without requiring a local Ollama server.");
                Check(await store.ClaimSoftwareJobAsync("worker", Ct) is null, "No model call is claimable before exact-payload Owner consent.");
                var job = run.Jobs.Single(); var preview = JsonSerializer.SerializeToElement(await store.PreviewSoftwareCloudAsync(company, run.Seed.RunId, job.JobId, Ct), RuntimeJson.Options);
                var hash = preview.GetProperty("payloadSha256").GetString()!;
                await Reject<WorkspaceConflictException>(() => store.ApproveSoftwareCloudAsync(company, run.Seed.RunId, job.JobId, new(run.LastSequence, "wrong"), Ct));
                run = await store.ApproveSoftwareCloudAsync(company, run.Seed.RunId, job.JobId, new(run.LastSequence, hash), Ct);
                var claim = (await store.ClaimSoftwareJobAsync("worker", Ct))!; claim = await store.PrepareSoftwareModelCallAsync(claim, Ct);
                using var vault = new RuntimeCredentialVault(); vault.Set(company, provider.ProviderId, provider.ProviderType, "fixture-api-key");
                var fake = new FixtureCloud(); using var ollama = new OllamaSoftwareClient("http://127.0.0.1:11436/");
                var routed = new RoutedSoftwareClient(ollama, store, vault, fake, new(root)); var binding = run.Seed.Bindings.Single(b => b.RoleId == "ba");
                var output = await routed.GenerateAsync(binding, claim, Ct);
                Check(fake.Calls == 1 && fake.Credential == "fixture-api-key" && fake.Payload == preview.GetProperty("payload").GetString(), "The worker dispatches exactly the approved provider payload using RAM credentials.");
                claim = await store.RecordSoftwareModelCallAsync(claim, output, Ct);
                await store.CompleteSoftwareJobAsync(claim, await coordinator.ProcessTypedOutputAsync(run, claim, output.Output, Ct), null, null, Ct);
                run = (await store.GetSoftwareRunAsync(company, run.Seed.RunId, Ct))!;
                Check(run.Spec?.Title == "API fixture spec" && run.Jobs.Any(j => j.NodeId == "leader" && j.Status == "waiting_input"), "Cloud output goes through the same typed validation and durable handoff as local output.");
                Check(!(await store.ReadEventsAsync(company, 0, Ct)).Any(e => e.Data.GetRawText().Contains("fixture-api-key")), "Keys never enter event history or persisted bindings.");
                var fenced = await coordinator.CreateAsync(company, new(demo.ProjectId, "Policy fencing"), "cloud-fenced", Ct);
                var pending = fenced.Jobs.Single(); var p = JsonSerializer.SerializeToElement(await store.PreviewSoftwareCloudAsync(company, fenced.Seed.RunId, pending.JobId, Ct), RuntimeJson.Options);
                await store.ApproveSoftwareCloudAsync(company, fenced.Seed.RunId, pending.JobId, new(fenced.LastSequence, p.GetProperty("payloadSha256").GetString()!), Ct);
                var stale = (await store.ClaimSoftwareJobAsync("worker", Ct))!;
                profile = (await store.WorkbenchAsync(company, Ct)).Profile;
                await store.ConfigureCompanyAsync(company, new(profile.Version, profile.Description, profile.Departments, false, "strict_local", profile.CloudPolicyVersion), Ct);
                await Reject<WorkspaceConflictException>(() => routed.GenerateAsync(fenced.Seed.Bindings.Single(b => b.RoleId == "ba"), stale, Ct));
                Check(fake.Calls == 1, "Switching to Strict Local fences already approved jobs before HTTP dispatch.");
                await store.SetMaintenanceAsync(true, Ct);
                await Reject<WorkspaceConflictException>(() => store.AddInteractionAsync(company, new("lobby", "inform", "owner", ["ba-agent"], "Must not write during update", [], []), "during-update", Ct));
                await store.SetMaintenanceAsync(false, Ct);
                Check((await store.VerifyAsync(Ct)).Companies.Length > 0, "Store integrity remains readable while the updater holds the write fence.");
                await Cli(root, provider);
            }
            finally
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                Directory.Delete(root, true);
            }
        }
        await BusyUpdate();
        Console.WriteLine($"PASS: {_checks} guided/cloud/update assertions: frozen cloud routes, exact consent, real bounded CLI subprocess fixture, key isolation, policy fences and maintenance integrity.");
    }
    private static async Task BusyUpdate()
    {
        var prefix = Path.Combine(Path.GetTempPath(), "aicos-busy-update-" + Guid.NewGuid().ToString("N")); var data = Path.Combine(prefix,"data");
        var oldRoot = Environment.GetEnvironmentVariable("AICOS_INSTALL_ROOT"); var oldPrefix = Environment.GetEnvironmentVariable("AICOS_INSTALL_PREFIX");
        try
        {
            Environment.SetEnvironmentVariable("AICOS_INSTALL_ROOT", prefix); Environment.SetEnvironmentVariable("AICOS_INSTALL_PREFIX", prefix);
            using var store = new FileEventStore(data); await store.InitializeAsync(Ct);
            foreach (var id in new[] { "ba-agent", "leader-agent" }) await store.ConfigureAgentAsync("software-studio", id, new(0,"Research","fixture:latest",[],10000,1024), Ct);
            await store.StartTeamDiscussionAsync("software-studio", new("lobby","main","A queued consultation must block updates.",[new("ba-agent","ba"),new("leader-agent","leader")]), [new("fixture:latest","fixture-digest","fixture",DateTimeOffset.UtcNow)],"busy-update",Ct);
            var lifetime = new Lifetime();
            await Reject<WorkspaceConflictException>(() => new UpdateService(data).ApplyAsync(new("build-2","fixture"),store,lifetime,Ct));
            Check(!store.Maintenance && !lifetime.Stopped && !File.Exists(Path.Combine(prefix,"updates","apply-request.json")), "Busy update rejection restores write availability and never requests process shutdown.");
        }
        finally { Environment.SetEnvironmentVariable("AICOS_INSTALL_ROOT",oldRoot); Environment.SetEnvironmentVariable("AICOS_INSTALL_PREFIX",oldPrefix); if (Directory.Exists(prefix)) Directory.Delete(prefix,true); }
    }
    private sealed class Lifetime : IHostApplicationLifetime
    { public bool Stopped; public CancellationToken ApplicationStarted => CancellationToken.None; public CancellationToken ApplicationStopping => CancellationToken.None; public CancellationToken ApplicationStopped => CancellationToken.None; public void StopApplication() => Stopped=true; }
    private static async Task Cli(string root, CloudProviderConfiguration provider)
    {
        var executable = Environment.ProcessPath!; var entry = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? typeof(GuidedUpgradeTests).Assembly.Location : null;
        string Hash(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
        var snapshot = new CloudProviderSnapshot(provider.ProviderId, provider.Version, provider.ProviderType, provider.ModelId, provider.EndpointHost, provider.EndpointPath, provider.ApiVersion, "memory", provider.RetentionCaveat);
        var route = new AgentCloudRoute(snapshot, "claude-code", executable, Hash(executable), entry, entry is null ? null : Hash(entry));
        var payload = JsonSerializer.Serialize(new { instruction = "Return the fixture spec.", context = "A trusted protocol fixture, not an installed LLM", schema = SoftwareContext.Schema("ba"), maxOutputTokens = 1024 });
        var ambient = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL"); Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", "https://must-not-be-inherited.invalid");
        try
        {
            var result = await new ClaudeCodeClient(root).ExecuteAsync(route, payload, "fixture-cli-key", Ct);
            Check(JsonDocument.Parse(result.Text).RootElement.GetProperty("title").GetString() == "CLI fixture spec" && result.Usage.OutputTokens == 20, "A real subprocess receives only the selected key/context, disabled tools and bounded structured-output flags; ambient provider endpoints are stripped.");
            Check(!Directory.EnumerateFileSystemEntries(Path.Combine(root, "cli-sessions")).Any(), "Temporary CLI sessions are removed after completion.");
            await Reject<CloudProviderException>(() => new ClaudeCodeClient(root).ExecuteAsync(route with { ExecutableSha256 = "tampered" }, payload, "fixture-cli-key", Ct));
        }
        finally { Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", ambient); }
    }
    private sealed class NoLocalCalls : ISoftwareModelClient
    { public int ListCalls; public Task<LocalModel[]> ListAsync(CancellationToken ct) { ListCalls++; throw new Exception("Unexpected Ollama dependency"); } public Task<SoftwareModelResult> GenerateAsync(RoleBinding binding, SoftwareJob job, CancellationToken ct) => throw new Exception("Unexpected local generation"); }
    private sealed class FixtureCloud : ICloudProviderClient
    {
        public int Calls; public string? Payload, Credential;
        public Task<CloudProviderResult> ExecuteAsync(CloudProviderSnapshot provider, string payload, string credential, CancellationToken ct)
        { Calls++; Payload = payload; Credential = credential; var text = JsonSerializer.Serialize(new RequirementSpec("API fixture spec", ["Reject empty input"], [new("AC-001", "Reject empty values", "test")], [], []), RuntimeJson.Options); return Task.FromResult(new CloudProviderResult(text, new(30,20), RuntimeJson.TextHash(text), "fixture-metadata")); }
    }
}
