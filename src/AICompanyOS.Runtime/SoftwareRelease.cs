using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AICompanyOS.Runtime;

public sealed record ReleasePreview(string CandidateSha256, string Branch, string? RemoteUrl, string BaseBranch, string ManifestSha256, string[] ChangedPaths, bool GitHubAllowed);

public sealed class SoftwareReleaseService(FileEventStore store, RepositoryService repositories, string dataRoot)
{
    private readonly TrustedProcess _process = new(Path.Combine(Path.GetFullPath(dataRoot), "execution", "release-client"));
    public static ReleasePreview Preview(SoftwareRun run, bool allowed)
    {
        if (run.Candidate is null || !SoftwareChecks.GatesPass(run)) throw new WorkspaceConflictException("A passing candidate is required.");
        var branch = "aicos/" + run.Seed.CompanyId + "/" + run.Seed.RunId;
        var hash = RuntimeJson.Hash(new { run.Candidate.Sha256, branch, run.Seed.Project.RemoteUrl, run.Seed.Project.BaseBranch, receipts = SoftwareChecks.CurrentReceipts(run).Select(r => r.LogSha256), allowed });
        return new(run.Candidate.Sha256, branch, run.Seed.Project.RemoteUrl, run.Seed.Project.BaseBranch, hash, run.Candidate.ChangedPaths, allowed);
    }
    public async Task<SoftwareRun> ExecuteAsync(string company, string runId, SoftwareReleaseRequest request, string key, CancellationToken ct)
    {
        var workbench = await store.WorkbenchAsync(company, ct); var run = workbench.Runs.FirstOrDefault(r => r.Seed.RunId == runId) ?? throw new KeyNotFoundException();
        if (run.Release is not null && run.State == "completed") return run;
        var preview = Preview(run, workbench.Profile.AllowGitHubRelease);
        if (run.State != "awaiting_release" || run.LastSequence != request.ExpectedSequence || request.CandidateSha256 != run.Candidate!.Sha256 ||
            !request.AcceptanceConfirmed || !request.RightsConfirmed || request.TargetManifestSha256 != preview.ManifestSha256 || request.PushRequested && (!preview.GitHubAllowed || preview.RemoteUrl is null))
            throw new WorkspaceConflictException("Confirm the exact release target, passing candidate, acceptance and rights. Remote GitHub release requires company policy and an explicit request.");
        if (request.PushRequested && (request.GitHubToken is not { Length: >= 20 and <= 300 } || request.GitHubToken.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_')))
            throw new ArgumentException("Remote release requires an ephemeral GitHub token; it is never persisted or sent to a model.");
        await store.PrepareSoftwareReleaseAsync(company, runId, request, preview.ManifestSha256, key, ct);
        var outcome = await repositories.CommitAsync(run, ct);
        await store.RecordSoftwareReleaseCommitAsync(company, runId, outcome, ct);
        var current = await store.GetSoftwareRunAsync(company, runId, ct) ?? throw new KeyNotFoundException();
        if (current.State != "awaiting_release") throw new WorkspaceConflictException("The scoped commit exists; workflow control changed. Reconcile before requesting remote release.");
        if (request.PushRequested)
        {
            var fresh = await store.WorkbenchAsync(company, ct);
            if (!fresh.Profile.AllowGitHubRelease || fresh.Runs.Single(r => r.Seed.RunId == runId).State != "awaiting_release") throw new WorkspaceConflictException("Remote release policy or checkpoint changed; the scoped local commit is retained.");
            outcome = await PushGitHubAsync(run, outcome, request.GitHubToken!, ct);
        }
        return await store.RecordSoftwareReleaseAsync(company, runId, preview.CandidateSha256, outcome, ct);
    }
    private async Task<ReleaseOutcome> PushGitHubAsync(SoftwareRun run, ReleaseOutcome release, string token, CancellationToken ct)
    {
        var remote = run.Seed.Project.RemoteUrl!;
        if (!RepositoryService.ValidGitHubRemote(remote)) throw new ArgumentException("Invalid GitHub destination.");
        var segments = new Uri(remote).AbsolutePath.Trim('/').Split('/'); var owner = segments[0]; var repository = segments[1][..^4];
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = new("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AICompanyOS/2.0"); http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        var api = "repos/" + Uri.EscapeDataString(owner) + "/" + Uri.EscapeDataString(repository);
        // Refuse to transfer an unrelated repository/history: the reviewed base must already be an ancestor of the destination base branch.
        using var compare = await http.GetAsync(api + "/compare/" + run.Seed.Repository.BaseCommit + "..." + Uri.EscapeDataString(run.Seed.Project.BaseBranch), ct);
        if (!compare.IsSuccessStatusCode) throw new ExecutionBlockedException("REMOTE_BASE_NOT_VERIFIED");
        using var compared = JsonDocument.Parse(await compare.Content.ReadAsStringAsync(ct));
        if (compared.RootElement.GetProperty("status").GetString() is not ("ahead" or "identical")) throw new WorkspaceConflictException("Destination base does not contain the pinned source base.");
        var root = Path.Combine(Path.GetFullPath(dataRoot), "execution", "releases", run.Seed.CompanyId, run.Seed.RunId); RepositoryService.NoLinks(root); Directory.CreateDirectory(root);
        var init = await _process.RunAsync("git", ["-c", "core.hooksPath=" + Path.Combine(root, "no-hooks"), "init", "--bare", root], root, 15, ct);
        if (init.ExitCode != 0) throw new ExecutionBlockedException("RELEASE_CLIENT_INIT_FAILED");
        var objects = await repositories.GitAsync(run.Seed.Project.RepositoryPath, ["rev-parse", "--git-path", "objects"], ct);
        if (objects.ExitCode != 0) throw new ExecutionBlockedException("RELEASE_OBJECT_SOURCE_FAILED");
        var objectPath = Path.GetFullPath(objects.Stdout.Trim(), run.Seed.Project.RepositoryPath); RepositoryService.NoLinks(objectPath);
        var info = Path.Combine(root, "objects", "info"); Directory.CreateDirectory(info); RepositoryService.NoLinks(info);
        await LocalFiles.WriteAtomicAsync(Path.Combine(info, "alternates"), Encoding.UTF8.GetBytes(objectPath + "\n"), ct);
        var authorization = "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + token));
        var environment = new Dictionary<string, string> { ["GIT_CONFIG_COUNT"] = "1", ["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraHeader", ["GIT_CONFIG_VALUE_0"] = authorization };
        string[] Prefix() => ["--no-pager", "--git-dir=" + root, "-c", "core.hooksPath=" + Path.Combine(root, "no-hooks"), "-c", "credential.helper=", "-c", "http.sslVerify=true", "-c", "protocol.ext.allow=never", "-c", "protocol.file.allow=never", "-c", "protocol.http.allow=never"];
        var target = "refs/heads/" + release.Branch;
        var prior = await _process.RunAsync("git", [.. Prefix(), "ls-remote", "--heads", "--exit-code", remote, target], root, 30, ct, extraEnvironment: environment, outputLimit: 65536);
        if (prior.ExitCode == 0 && prior.Stdout.Split('\t')[0].Trim() != release.CommitSha) throw new WorkspaceConflictException("The scoped remote branch has different content; no existing branch will be overwritten.");
        if (prior.ExitCode is not (0 or 2)) throw new ExecutionBlockedException("REMOTE_REF_UNVERIFIABLE");
        if (prior.ExitCode == 2)
        {
            var pushed = await _process.RunAsync("git", [.. Prefix(), "push", "--porcelain", "--force-with-lease=" + target + ":" + new string('0', 40), remote, release.CommitSha + ":" + target], root, 60, ct, extraEnvironment: environment, outputLimit: 65536);
            if (pushed.ExitCode != 0) throw new ExecutionBlockedException("PUSH_NOT_CONFIRMED_RECONCILE_TARGET");
        }
        var verified = await _process.RunAsync("git", [.. Prefix(), "ls-remote", "--heads", remote, target], root, 30, ct, extraEnvironment: environment, outputLimit: 65536);
        if (verified.ExitCode != 0 || verified.Stdout.Split('\t')[0].Trim() != release.CommitSha) throw new ExecutionBlockedException("PUSH_NOT_CONFIRMED_RECONCILE_TARGET");
        var head = Uri.EscapeDataString(owner + ":" + release.Branch);
        using var existing = await http.GetAsync(api + "/pulls?state=open&head=" + head, ct);
        if (!existing.IsSuccessStatusCode) throw new ExecutionBlockedException("PUSHED_PR_LOOKUP_FAILED");
        using var prs = JsonDocument.Parse(await existing.Content.ReadAsStringAsync(ct)); string? url;
        if (prs.RootElement.GetArrayLength() > 0)
        {
            var pr = prs.RootElement[0];
            if (pr.GetProperty("head").GetProperty("sha").GetString() != release.CommitSha) throw new WorkspaceConflictException("Existing PR head differs from the reviewed commit.");
            url = pr.GetProperty("html_url").GetString();
        }
        else
        {
            using var content = new StringContent(JsonSerializer.Serialize(new { title = "AI Company delivery: " + run.Seed.Objective[..Math.Min(run.Seed.Objective.Length, 120)],
                head = release.Branch, @base = run.Seed.Project.BaseBranch, draft = true,
                body = "Owner-approved candidate " + run.Candidate!.Sha256 + ".\n\nRecorded receipts: " + string.Join(", ", SoftwareChecks.CurrentReceipts(run).Select(r => r.ReceiptId)) + ".\n\nReview semantic acceptance, model output and provenance before merge. No automatic merge or deployment." }, RuntimeJson.Options), Encoding.UTF8, "application/json");
            using var created = await http.PostAsync(api + "/pulls", content, ct);
            if (!created.IsSuccessStatusCode) throw new ExecutionBlockedException("PUSHED_PR_CREATE_NOT_CONFIRMED");
            using var result = JsonDocument.Parse(await created.Content.ReadAsStringAsync(ct)); url = result.RootElement.GetProperty("html_url").GetString();
        }
        return release with { Pushed = true, PullRequestUrl = url };
    }
}

public sealed partial class FileEventStore
{
    public async Task PrepareSoftwareReleaseAsync(string company, string id, SoftwareReleaseRequest request, string manifest, string key, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(company, 0, ct); var run = RebuildSoftwareRun(company, id, events.Where(e => e.RunId == id).ToArray()) ?? throw new KeyNotFoundException();
            if (run.State != "awaiting_release" || run.LastSequence != request.ExpectedSequence || run.Candidate?.Sha256 != request.CandidateSha256 || request.PushRequested && !ProfileFrom(events).AllowGitHubRelease) throw new WorkspaceConflictException("Release checkpoint or company policy changed.");
            var hash = RuntimeJson.Hash(new { request.CandidateSha256, request.PushRequested, manifest });
            var previous = events.FirstOrDefault(e => e.RunId == id && e.Type == "software.release.prepared" && ReadString(e.Data, "key") == key);
            if (previous is not null && ReadString(previous.Data, "hash") != hash) throw new WorkspaceConflictException("Release key belongs to another exact target.");
            if (previous is null) await AppendUnsafeAsync(company, id, [DomainEvent.Create("software.release.prepared", new { key, hash, manifest, request.CandidateSha256, request.PushRequested, status = "requires_reconciliation_after_ambiguous_failure" })], events, ct);
        }
        finally { _gate.Release(); }
    }
    public async Task RecordSoftwareReleaseCommitAsync(string company, string id, ReleaseOutcome release, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(company, 0, ct);
            if (!events.Any(e => e.RunId == id && e.Type == "software.release.commit" && ReadString(e.Data, "commitSha") == release.CommitSha))
                await AppendUnsafeAsync(company, id, [DomainEvent.Create("software.release.commit", release)], events, ct);
        }
        finally { _gate.Release(); }
    }
}
