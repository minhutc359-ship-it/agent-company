namespace AICompanyOS.Runtime;

public sealed partial class RepositoryService
{
    public async Task<string> ReconcileWorktreeAsync(SoftwareRun run, SoftwareJob job, CancellationToken ct)
    {
        if (job.TaskId is null || job.Status is not ("failed" or "interrupted") || job.Error == "OWNER_PAUSE" || run.Patches.Any(p => p.JobId == job.JobId))
            throw new WorkspaceConflictException("Only an unaccepted failed/interrupted Dev worktree can be discarded.");
        var parent = LocalFiles.Child(_root, "worktrees", run.Seed.CompanyId, run.Seed.RunId);
        var directory = LocalFiles.Child(parent, job.JobId); var marker = LocalFiles.Child(parent, job.JobId + ".json");
        NoLinks(directory); NoLinks(marker);
        var registered = await GitAsync(run.Seed.Project.RepositoryPath, ["worktree", "list", "--porcelain", "-z"], ct);
        if (registered.ExitCode != 0) throw new ExecutionBlockedException("WORKTREE_REGISTRY_UNAVAILABLE");
        var paths = registered.Stdout.Split('\0').Where(s => s.StartsWith("worktree ", StringComparison.Ordinal)).Select(s => Path.GetFullPath(s[9..]));
        var known = paths.Contains(Path.GetFullPath(directory), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (Directory.Exists(directory) && !known) throw new WorkspaceConflictException("The temporary directory is not a registered managed worktree; inspect it locally before removing anything.");
        if (known)
        {
            var removed = await GitAsync(run.Seed.Project.RepositoryPath, ["worktree", "remove", "--force", directory], ct);
            if (removed.ExitCode != 0 || Directory.Exists(directory)) throw new ExecutionBlockedException("WORKTREE_RECONCILIATION_UNCONFIRMED");
        }
        if (File.Exists(marker)) File.Delete(marker);
        return directory;
    }
}
public sealed partial class FileEventStore
{
    public async Task<SoftwareRun> RecordWorktreeReconciliationAsync(string company, string id, string jobId, long sequence, string directory, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(company, 0, ct); var run = RebuildSoftwareRun(company, id, events.Where(e => e.RunId == id).ToArray()) ?? throw new KeyNotFoundException();
            if (run.LastSequence != sequence || run.State != "blocked" || !run.Jobs.Any(j => j.JobId == jobId && j.Status is "failed" or "interrupted")) throw new WorkspaceConflictException("Recovery checkpoint changed; reload before retrying.");
            await AppendUnsafeAsync(company, id, [DomainEvent.Create("software.worktree.reconciled", new { jobId, directory, sourceWorkingDirectoryUpdated = false })], events, ct);
            return RebuildSoftwareRun(company, id, events.Where(e => e.RunId == id).ToArray())!;
        }
        finally { _gate.Release(); }
    }
}
