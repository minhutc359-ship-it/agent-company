using AICompanyOS.Runtime;

public static class StarterTests
{
    private static int _checks;
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static void Check(bool value, string message) { _checks++; if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { _checks++; return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    public static async Task RunAsync()
    {
        var pack = CompanyStarters.SoftwareTeam;
        Check(pack.Agents.Length == 8 && pack.Skills.Length == 7 && pack.Workflows.All(w => SoftwareContracts.Compile(w).Valid), "Starter assets must cover the complete executable software contract.");
        foreach (var backend in new[] { "file", "sqlite" })
        {
            var root = Path.Combine(Path.GetTempPath(), "aicos-starter-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var store = new FileEventStore(root, backend))
                {
                    await store.InitializeAsync(Ct);
                    await store.CreateCompanyAsync(new("other-company", "Isolated"), Ct);
                    var custom = await store.ConfigureAgentAsync("software-studio", "dev-agent", new(0, "Custom", null, [], 5000, 1024), Ct);
                    var identity = await store.UpdateAgentIdentityAsync("software-studio", "dev-agent", new("Dev chính", ["dev", "tester"], 1), Ct);
                    await Reject<WorkspaceConflictException>(() => store.UpdateAgentIdentityAsync("software-studio", "dev-agent", new("Stale name", ["dev"], 1), Ct));
                    var before = await store.ReadEventsAsync("software-studio", 0, Ct);
                    await Reject<ArgumentException>(() => store.InstallCompanyStarterAsync("software-studio", new(pack.PackId, "unknown:latest"), "unknown-model", [], Ct));
                    Check((await store.ReadEventsAsync("software-studio", 0, Ct)).Count == before.Count, "Invalid starter input must have no partial effects.");
                    var result = await store.InstallCompanyStarterAsync("software-studio", new(pack.PackId), "starter-command", [], Ct);
                    Check(result.AddedAgents == 1 && result.ConfiguredAgents == 7 && result.InstalledSkills == 7 && result.SavedWorkflows == 3, "Starter installs only missing identities, configurations and immutable versions.");
                    var workbench = await store.WorkbenchAsync("software-studio", Ct); var agents = await store.ListAgentsAsync("software-studio", Ct);
                    Check(agents.Length == 8 && agents.All(a => a.ToolGrants.Length == 0), "Presets never grant tools or Owner capability.");
                    Check(agents.Single(a => a.AgentId == "dev-agent").Name == identity.Name && agents.Single(a => a.AgentId == "dev-agent").RoleIds.SequenceEqual(identity.RoleIds), "Starter install preserves edited names and multi-role assignments without granting tools.");
                    Check(RuntimeJson.Hash(workbench.AgentConfigurations.Single(c => c.AgentId == "dev-agent")) == RuntimeJson.Hash(custom), "An existing customized model/budget/skill route must be preserved.");
                    Check(workbench.Profile.PrivacyMode == "strict_local" && workbench.Runs.Length == 0 && workbench.AgentConfigurations.All(c => c.ModelId is null), "Preset setup cannot start a run, enable cloud or invent an installed model.");
                    var sequence = (await store.ReadEventsAsync("software-studio", 0, Ct)).Count;
                    Check(RuntimeJson.Hash(await store.InstallCompanyStarterAsync("software-studio", new(pack.PackId), "starter-command", [], Ct)) == RuntimeJson.Hash(result), "Command replay returns its original result.");
                    Check((await store.ReadEventsAsync("software-studio", 0, Ct)).Count == sequence, "Command replay cannot append duplicate versions.");
                    await Reject<WorkspaceConflictException>(() => store.InstallCompanyStarterAsync("software-studio", new(pack.PackId, "local:latest"), "starter-command", [new("local:latest", "digest", "fixture", DateTimeOffset.UtcNow)], Ct));
                    var repeated = await store.InstallCompanyStarterAsync("software-studio", new(pack.PackId), "starter-again", [], Ct);
                    Check(repeated.ConfiguredAgents == 0 && repeated.AddedAgents == 0 && repeated.InstalledSkills == 0 && repeated.SavedWorkflows == 0, "Installing again preserves all current customizations.");
                    Check((await store.WorkbenchAsync("other-company", Ct)).Skills.Length == 0 && (await store.ListAgentsAsync("other-company", Ct)).Length == 7, "A preset is isolated to the selected company.");
                }
                using (var restored = new FileEventStore(root, backend))
                {
                    var workbench = await restored.WorkbenchAsync("software-studio", Ct);
                    Check(workbench.Skills.Length == 7 && workbench.Workflows.Length == 4 && workbench.AgentConfigurations.Length == 8, "Starter configuration and definitions survive restart on both stores.");
                }
            }
            finally { Directory.Delete(root, true); }
        }
        Console.WriteLine($"PASS: {_checks} starter-pack assertions: immutable templates, company isolation, preserved customization, no implicit inference/grants, idempotency and restart parity.");
    }
}
