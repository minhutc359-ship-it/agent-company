using System.Text.Json;

namespace AICompanyOS.Runtime;

public sealed class SoftwareCoordinator(FileEventStore store, RepositoryService repositories, DockerSandbox sandbox, ISoftwareModelClient models)
{
    public async Task<SoftwareRun> CreateAsync(string company, SoftwareCreateRequest request, string key, CancellationToken ct)
    {
        var workbench = await store.WorkbenchAsync(company, ct);
        var project = workbench.Projects.FirstOrDefault(p => p.ProjectId == request.ProjectId) ?? throw new KeyNotFoundException();
        var snapshot = await repositories.SnapshotAsync(project, ct);
        var catalog = workbench.AgentConfigurations.Any(a => a.ModelId is not null) ? await models.ListAsync(ct) : [];
        return await store.CreateSoftwareRunAsync(company, request, project.Version, snapshot, catalog, key, ct);
    }
    public async Task<SoftwareRun> PreviewAsync(string company, string id, long expectedSequence, CancellationToken ct)
    {
        var run = await store.GetSoftwareRunAsync(company, id, ct) ?? throw new KeyNotFoundException();
        if (await repositories.ValidateRepositoryAsync(run.Seed.Project.RepositoryPath, ct) != run.Seed.Repository.BaseCommit) throw new WorkspaceConflictException("Repository HEAD changed after intake; create a reviewed run from the new commit.");
        var capability = await sandbox.ProbeAsync(run.Seed.Project.DockerImage, ct);
        if (!capability.Available || capability.ImageId is null) throw new ExecutionBlockedException(capability.Reason);
        return await store.PreviewExecutionGrantAsync(company, id, expectedSequence, capability.ImageId, ct);
    }
    public async Task<object> ProcessTypedOutputAsync(SoftwareRun run, SoftwareJob job, JsonElement output, CancellationToken ct)
    {
        if (job.NodeId == "ba") return SoftwareContracts.ValidateSpec(SoftwareJson.Parse<RequirementSpec>(output));
        if (job.NodeId == "leader") return SoftwareContracts.ValidatePlan(SoftwareJson.Parse<TaskGraph>(output), run.Spec!, run.Seed.Project, run.Seed.Bindings);
        if (job.TaskId is null) throw new ArgumentException("This node does not accept model/Owner JSON output.");
        var patch = SoftwareJson.Parse<RepositoryPatch>(output);
        if (patch.ReadRequests is { Length: > 0 }) throw new ArgumentException("A file-read request is not a completed patch.");
        var task = run.Plan!.Tasks.Single(t => t.Id == job.TaskId); var baseline = SoftwareContext.TaskBaseline(run, task.Id);
        var files = RepositoryService.Apply(baseline, patch, task.AllowedPaths);
        if (patch.Files.Any(f => RepositoryService.TouchesOmitted(f.Path, run.Seed.Repository.OmittedPaths))) throw new ArgumentException("An omitted, binary or denied base path cannot be treated as a new text file.");
        if (job.WorkerId != "owner-supplied")
        {
            var provided = SoftwareContext.ProvidedPaths(job.Input);
            if (patch.Files.Any(f => baseline.Any(b => b.Path == f.Path) && !provided.Contains(f.Path))) throw new ExecutionBlockedException("EXISTING_FILE_NOT_READ_IN_CONTEXT");
        }
        var started = DateTimeOffset.UtcNow;
        await store.PrepareSoftwarePatchAsync(job, patch, ct);
        if (!await store.HeartbeatSoftwareJobAsync(job, ct)) throw new ExecutionBlockedException("WORKTREE_LEASE_REPLACED");
        var directory = await repositories.MaterializeWorktreeAsync(run, job, files, ct);
        var stdout = JsonSerializer.Serialize(new { operation = "materialize_and_verify_isolated_worktree", directory, run.Seed.Repository.BaseCommit,
            changedFiles = patch.Files.Select(f => new { f.Path, f.BeforeSha256, afterSha256 = RuntimeJson.TextHash(f.Content) }), sourceWorkingDirectoryUpdated = false }, RuntimeJson.Options);
        var receipt = new ExecutionReceipt("receipt-" + Guid.NewGuid().ToString("N"), job.JobId, job.RoleId, job.AgentId, "worktree.patch", "2.0.0", RuntimeJson.Hash(patch),
            "executed", 0, started, DateTimeOffset.UtcNow, stdout, "", SoftwareChecks.LogHash(stdout, "", [], null, null), null, null, [], Attempt: job.Attempts);
        return new TaskPatch(task.Id, job.JobId, job.Round, patch, RuntimeJson.Hash(patch), directory, DateTimeOffset.UtcNow, receipt);
    }
    public async Task<SoftwareRun> SupplyOutputAsync(string company, string id, string node, SoftwareOutputRequest request, CancellationToken ct)
    {
        var claim = await store.ClaimManualSoftwareOutputAsync(company, id, node, request.ExpectedSequence, ct);
        try
        {
            var run = (await store.GetSoftwareRunAsync(company, id, ct))!;
            var result = await ProcessTypedOutputAsync(run, claim, request.Output, ct);
            await store.CompleteSoftwareJobAsync(claim, result, null, null, ct);
        }
        catch (Exception e) when (e is ArgumentException or JsonException or WorkspaceConflictException or ExecutionBlockedException)
        {
            await store.CompleteSoftwareJobAsync(claim, null, null, "OWNER_OUTPUT_INVALID:" + (e is ExecutionBlockedException ? e.Message : "SCHEMA_OR_SCOPE"), ct);
            throw;
        }
        return (await store.GetSoftwareRunAsync(company, id, ct))!;
    }
}

public sealed class SoftwareWorker(FileEventStore store, SoftwareCoordinator coordinator, DockerSandbox sandbox, ISoftwareModelClient models, ILogger<SoftwareWorker> logger) : BackgroundService
{
    public string WorkerId { get; } = "software-worker-" + Guid.NewGuid().ToString("N");
    private SoftwareJob[] _orphans = [];
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _orphans = (await store.RecoverSoftwareJobsAsync(WorkerId, stoppingToken)).Where(j => j.NodeId is "build" or "tester").ToArray();
        var active = new List<Task>();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var finished in active.Where(t => t.IsCompleted).ToArray()) { active.Remove(finished); try { await finished; } catch (Exception) { logger.LogError("Software job could not commit; persisted lease and receipts remain authoritative."); } }
                if (active.Count < 4)
                {
                    var job = await store.ClaimSoftwareJobAsync(WorkerId, stoppingToken);
                    if (job is not null) { active.Add(RunClaimAsync(job, stoppingToken)); continue; }
                }
                await Task.WhenAny([.. active, Task.Delay(500, stoppingToken)]);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogError("Software scheduler could not advance; no success is implied."); await Task.Delay(1000, stoppingToken); }
        }
        try { await Task.WhenAll(active); } catch (Exception) { }
    }
    public async Task<bool> RunOneAsync(CancellationToken ct)
    {
        var claim = await store.ClaimSoftwareJobAsync(WorkerId, ct); if (claim is null) return false;
        await RunClaimAsync(claim, ct); return true;
    }
    private async Task RunClaimAsync(SoftwareJob initial, CancellationToken stoppingToken)
    {
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); invocation.CancelAfter(TimeSpan.FromMinutes(10));
        var claim = initial;
        object? result = null; string? error = null;
        async Task<object> ExecuteAsync()
        {
            var run = (await store.GetSoftwareRunAsync(claim.CompanyId, claim.RunId, invocation.Token))!;
            if (claim.NodeId is "build" or "tester")
            {
                if (_orphans.Length > 0) { await sandbox.ReconcileAsync(_orphans, invocation.Token); _orphans = []; }
                if (run.Grant is null || run.Nodes.Single(n => n.Id == "approval-plan").Status != "approved") throw new ExecutionBlockedException("NO_APPROVED_EXECUTION_MANIFEST");
                return await sandbox.ExecuteAsync(run, claim, claim.NodeId == "build" ? run.Seed.Project.Build : run.Seed.Project.Test, run.Candidate!.Files, run.Grant.ImageId, invocation.Token);
            }
            if (claim.NodeId == "soc") return SoftwareChecks.Security(run, claim);
            if (claim.NodeId == "qa") return SoftwareChecks.Quality(run, claim);
            var binding = run.Seed.Bindings.Single(b => b.RoleId == claim.RoleId && b.Agent.AgentId == claim.AgentId);
            for (var call = 0; call < 3; call++)
            {
                claim = await store.PrepareSoftwareModelCallAsync(claim, invocation.Token);
                var output = await models.GenerateAsync(binding, claim, invocation.Token);
                claim = await store.RecordSoftwareModelCallAsync(claim, output, invocation.Token);
                if (claim.TaskId is not null)
                {
                    var patch = SoftwareJson.Parse<RepositoryPatch>(output.Output);
                    if (patch.ReadRequests is { Length: > 0 })
                    {
                        if (patch.Files.Length != 0) throw new ArgumentException("File-read requests and write patches must be separate calls.");
                        claim = await store.ExpandSoftwareContextAsync(claim, patch.ReadRequests, invocation.Token); continue;
                    }
                }
                return await coordinator.ProcessTypedOutputAsync(run, claim, output.Output, invocation.Token);
            }
            throw new ExecutionBlockedException("PROGRESSIVE_READ_CALL_LIMIT");
        }
        Task<object>? execution = null;
        try
        {
            if (!await store.HeartbeatSoftwareJobAsync(claim, invocation.Token)) return;
            execution = ExecuteAsync();
            while (!execution.IsCompleted)
            {
                var heartbeat = Task.Delay(5000, invocation.Token);
                if (await Task.WhenAny(execution, heartbeat) == execution) break;
                await heartbeat;
                if (!await store.HeartbeatSoftwareJobAsync(claim, invocation.Token)) { invocation.Cancel(); break; }
            }
            result = await execution.WaitAsync(invocation.Token);
        }
        catch (ModelProviderException failure) { error = failure.Code; }
        catch (ExecutionBlockedException blocked) { error = blocked.Message; }
        catch (OperationCanceledException) { error = stoppingToken.IsCancellationRequested ? "RUNTIME_STOPPED" : "CANCELLED_OR_TIMED_OUT"; }
        catch (Exception) { error = "NODE_OUTPUT_OR_EXECUTION_FAILED"; }
        invocation.Cancel();
        if (execution is not null && !execution.IsCompleted)
        {
            try { await execution.WaitAsync(TimeSpan.FromSeconds(25)); } catch (Exception) { }
        }
        if (execution is not null && !execution.IsCompleted && claim.NodeId is "build" or "tester") _orphans = [.. _orphans, claim];
        if (execution is not null) _ = execution.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        if (!stoppingToken.IsCancellationRequested) await store.CompleteSoftwareJobAsync(claim, result, null, error, stoppingToken);
    }
}

public sealed partial class FileEventStore
{
    public async Task PrepareSoftwarePatchAsync(SoftwareJob claim, RepositoryPatch patch, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(claim.CompanyId, 0, ct); var run = RebuildSoftwareRun(claim.CompanyId, claim.RunId, events.Where(e => e.RunId == claim.RunId).ToArray())!;
            var job = run.Jobs.Single(j => j.JobId == claim.JobId);
            if (run.State is "paused" or "cancelled" || job.Status != "running" || job.LeaseToken != claim.LeaseToken || job.LeaseUntil <= DateTimeOffset.UtcNow || job.TaskId is null || run.Grant is null || Node(run, "approval-plan").Status != "approved") throw new ExecutionBlockedException("PATCH_GRANT_OR_LEASE_INVALID");
            RepositoryService.Apply(SoftwareContext.TaskBaseline(run, job.TaskId), patch, run.Plan!.Tasks.Single(t => t.Id == job.TaskId).AllowedPaths);
            if (patch.Files.Any(f => RepositoryService.TouchesOmitted(f.Path, run.Seed.Repository.OmittedPaths))) throw new ArgumentException("Patch collides with an omitted base path.");
            await AppendUnsafeAsync(claim.CompanyId, claim.RunId, [DomainEvent.Create("software.patch.prepared", new { job.JobId, job.Round, job.InputSha256, patch, patchSha256 = RuntimeJson.Hash(patch) })], events, ct);
        }
        finally { _gate.Release(); }
    }
}
