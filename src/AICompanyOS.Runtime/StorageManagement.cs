using System.Text.Json;

namespace AICompanyOS.Runtime;

public sealed partial class FileEventStore
{
    private IEventJournal OpenJournal(StorageProfile profile)
    {
        var root = _root;
        if (profile.Generation is not null)
        {
            Identifiers.Require(profile.Generation);
            root = LocalFiles.Child(_root, "stores", profile.Generation);
        }
        return profile.Backend switch
        {
            "file" => new FileJournal(root),
            "sqlite" => new SqliteJournal(root),
            _ => throw new InvalidDataException("Unknown storage backend.")
        };
    }

    private static async Task<StorageManifest> ManifestAsync(IEventJournal journal, CancellationToken ct)
    {
        var companies = new List<CompanyIntegrity>();
        foreach (var companyId in journal.Companies().Order(StringComparer.Ordinal))
        {
            var events = await journal.ReadAsync(companyId, ct);
            companies.Add(new(companyId, events.Count, RuntimeJson.Hash(events), events.LastOrDefault()?.ContentSha256,
                events.Count(item => item.ContentSha256 is null)));
        }
        return new(journal.Backend, companies.ToArray(), RuntimeJson.Hash(companies));
    }

    public async Task<StorageManifest> VerifyAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await ManifestAsync(_journal, ct); }
        finally { _gate.Release(); }
    }

    public async Task<MigrationResult> MigrateAsync(string backend, string expectedDigest, CancellationToken ct)
    {
        if (backend is not ("file" or "sqlite")) throw new ArgumentException("Choose file or sqlite.");
        await _gate.WaitAsync(ct);
        try
        {
            var source = await ManifestAsync(_journal, ct);
            if (source.Digest != expectedDigest) throw new WorkspaceConflictException("Storage changed after preview; verify again.");
            if (backend == _journal.Backend) return new(backend, backend, source.Digest, source.Companies.Sum(c => c.EventCount), false);

            // Freeze all writes under the workspace lock, import into a new generation, then switch one pointer.
            // Old generations remain untouched for recovery. There is never a dual active writer.
            var targetProfile = new StorageProfile(backend, "store-" + Guid.NewGuid().ToString("N"), source.Backend, source.Digest, DateTimeOffset.UtcNow);
            IEventJournal? target = OpenJournal(targetProfile);
            try
            {
                foreach (var company in source.Companies)
                    await target.AppendAsync(company.CompanyId, 0, await _journal.ReadAsync(company.CompanyId, ct), ct);
                var verification = await ManifestAsync(target, ct);
                if (source.Digest != verification.Digest) throw new InvalidDataException("Migration checksum mismatch; the original storage remains active.");
                await LocalFiles.WriteAtomicAsync(LocalFiles.Child(_root, "storage.json"), JsonSerializer.SerializeToUtf8Bytes(targetProfile, JsonOptions), ct);
                var previous = _journal;
                _journal = target;
                _profile = targetProfile;
                target = null;
                previous.Dispose();
                return new(source.Backend, backend, verification.Digest, source.Companies.Sum(c => c.EventCount), true);
            }
            finally { target?.Dispose(); }
        }
        finally { _gate.Release(); }
    }

    public async Task<CommandResult> ControlRunAsync(string companyId, string runId, string action, long expectedSequence,
        string idempotencyKey, CancellationToken ct)
    {
        if (action is not ("pause" or "resume" or "cancel")) throw new ArgumentException("Invalid workflow control.");
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(companyId, 0, ct);
            var runEvents = events.Where(e => e.RunId == runId).ToArray();
            var run = RebuildRun(companyId, runId, runEvents);
            if (run is null) return new(CommandOutcome.NotFound, null);
            var prior = runEvents.FirstOrDefault(e => e.Type.StartsWith("workflow.") && ReadString(e.Data, "commandKey") == idempotencyKey);
            if (prior is not null) return ReadString(prior.Data, "action") == action
                ? new(CommandOutcome.AlreadyApplied, run)
                : new(CommandOutcome.Conflict, run, "Command key belongs to a different action.");
            if (run.LastSequence != expectedSequence) return new(CommandOutcome.Conflict, run, "The run changed; reload its checkpoint.");
            if (run.State is "cancelled" or "approved" or "revision_requested" or "delivered")
                return new(CommandOutcome.Conflict, run, "This workflow is terminal. Create a new run for revised work.");
            if (action == "resume" && run.State != "paused" || action == "pause" && run.State == "paused")
                return new(CommandOutcome.Conflict, run, "Invalid transition from the current checkpoint.");
            var type = action switch { "pause" => "workflow.paused", "resume" => "workflow.resumed", _ => "workflow.cancelled" };
            var additions = new List<DomainEvent> { DomainEvent.Create(type, new { action, commandKey = idempotencyKey, resumeState = run.State }) };
            if (run.ExecutionMode == "local_model")
            {
                var jobs = JobsFrom(runEvents);
                if (action is "pause" or "cancel")
                    foreach (var active in jobs.Where(j => j.Status == "running" || action == "cancel" && j.Status == "queued"))
                        additions.Add(DomainEvent.Create("invocation.interrupted", active with
                        { Status = action == "cancel" ? "cancelled" : "interrupted", LeaseToken = null, ErrorCode = action == "cancel" ? "OWNER_CANCELLED" : "OWNER_PAUSED" }));
                if (action == "resume" && run.ResumeState == "running")
                {
                    var interrupted = jobs.LastOrDefault(j => j.Status == "interrupted");
                    if (interrupted is not null)
                        additions.Add(interrupted.Attempts < MaximumModelAttempts
                            ? DomainEvent.Create("invocation.retry_requested", interrupted with { Status = "queued", LeaseToken = null, LeaseUntil = null, ErrorCode = null })
                            : DomainEvent.Create("invocation.failed", interrupted with { Status = "failed", ErrorCode = "ATTEMPT_BUDGET_EXHAUSTED" }));
                }
            }
            await AppendUnsafeAsync(companyId, runId, additions, events, ct);
            return new(CommandOutcome.Applied, RebuildRun(companyId, runId, events.Where(e => e.RunId == runId).ToArray()));
        }
        finally { _gate.Release(); }
    }
}

public sealed class WorkspaceConflictException(string message) : Exception(message);
