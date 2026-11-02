using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AICompanyOS.Runtime;

public sealed record ProcessCapture(int ExitCode, byte[] StdoutBytes, byte[] StderrBytes)
{
    public string Stdout => Encoding.UTF8.GetString(StdoutBytes);
    public string Stderr => Encoding.UTF8.GetString(StderrBytes);
}
public sealed class ExecutionBlockedException(string code) : Exception(code);

public sealed class TrustedProcess(string privateRoot)
{
    public async Task<ProcessCapture> RunAsync(string executable, IEnumerable<string> arguments, string directory, int timeoutSeconds, CancellationToken ct,
        byte[]? input = null, Dictionary<string, string>? extraEnvironment = null, int outputLimit = 2 * 1024 * 1024)
    {
        Directory.CreateDirectory(privateRoot);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        start.Environment.Clear();
        foreach (var key in new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP" }) if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        start.Environment["HOME"] = privateRoot; start.Environment["USERPROFILE"] = privateRoot;
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1"; start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(privateRoot, "empty.gitconfig");
        start.Environment["GIT_TERMINAL_PROMPT"] = "0"; start.Environment["GIT_ATTR_NOSYSTEM"] = "1";
        start.Environment["GIT_LFS_SKIP_SMUDGE"] = "1"; start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        start.Environment["DOCKER_CONFIG"] = privateRoot;
        if (extraEnvironment is not null) foreach (var (key, value) in extraEnvironment) start.Environment[key] = value;
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new ExecutionBlockedException("PROCESS_NOT_AVAILABLE"); }
        catch (System.ComponentModel.Win32Exception) { throw new ExecutionBlockedException("EXECUTABLE_NOT_AVAILABLE:" + executable); }
        using var registration = deadline.Token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        async Task<byte[]> ReadAsync(Stream stream)
        {
            using var bytes = new MemoryStream(); var buffer = new byte[4096]; int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (bytes.Length + read > outputLimit) { deadline.Cancel(); throw new ExecutionBlockedException("PROCESS_OUTPUT_LIMIT"); }
                bytes.Write(buffer, 0, read);
            }
            return bytes.ToArray();
        }
        var stdout = ReadAsync(process.StandardOutput.BaseStream); var stderr = ReadAsync(process.StandardError.BaseStream);
        try
        {
            if (input is not null) await process.StandardInput.BaseStream.WriteAsync(input, deadline.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(stdout, stderr);
            return new(process.ExitCode, stdout.Result, stderr.Result);
        }
        catch
        {
            deadline.Cancel();
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
            try { await Task.WhenAll(stdout, stderr); } catch (Exception) { }
            throw;
        }
    }
}

public sealed partial class RepositoryService
{
    private readonly string _root;
    private readonly TrustedProcess _process;
    public RepositoryService(string dataRoot)
    {
        _root = LocalFiles.Child(Path.GetFullPath(dataRoot), "execution"); Directory.CreateDirectory(_root);
        _process = new(LocalFiles.Child(_root, "process-home"));
    }
    [GeneratedRegex(@"^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\.git$", RegexOptions.CultureInvariant)]
    private static partial Regex RemotePattern();
    public static bool ValidGitHubRemote(string url) => RemotePattern().IsMatch(url) && !url.Contains("..", StringComparison.Ordinal);
    public static void NoLinks(string path)
    {
        var full = Path.GetFullPath(path); var cursor = Path.GetPathRoot(full)!;
        foreach (var part in full[Path.GetPathRoot(full)!.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            cursor = Path.Combine(cursor, part);
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new ExecutionBlockedException("SYMLINK_PATH_DENIED");
        }
    }
    private async Task<List<string>> GitPrefixAsync(string repo, CancellationToken ct)
    {
        var args = new List<string> { "--no-pager", "--no-optional-locks", "-c", "core.hooksPath=" + LocalFiles.Child(_root, "no-hooks"), "-c", "core.fsmonitor=false", "-c", "commit.gpgSign=false", "-c", "core.untrackedCache=false", "-c", "protocol.ext.allow=never", "-c", "core.attributesFile=" + LocalFiles.Child(_root, "no-attributes") };
        var filters = await _process.RunAsync("git", [.. args, "-C", repo, "config", "--name-only", "--get-regexp", "^filter\\."], _root, 10, ct, outputLimit: 65536);
        foreach (var key in filters.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var k = key.Trim(); var end = k.LastIndexOf('.');
            if (end <= 7 || !k.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) throw new ExecutionBlockedException("UNSUPPORTED_GIT_FILTER");
            var driver = k[..end];
            foreach (var suffix in new[] { "clean=", "smudge=", "process=", "required=false" }) { args.Add("-c"); args.Add(driver + "." + suffix); }
        }
        args.Add("-C"); args.Add(repo); return args;
    }
    public async Task<ProcessCapture> GitAsync(string repo, string[] arguments, CancellationToken ct, byte[]? input = null, Dictionary<string, string>? environment = null)
    {
        NoLinks(repo); var prefix = await GitPrefixAsync(repo, ct);
        return await _process.RunAsync("git", [.. prefix, .. arguments], _root, 30, ct, input, environment);
    }
    public async Task<string> ValidateRepositoryAsync(string path, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)) throw new ArgumentException("Choose an existing absolute local repository directory.");
        NoLinks(path);
        if (File.Exists(Path.Combine(path, ".git")) || Directory.Exists(Path.Combine(path, ".git"))) NoLinks(Path.Combine(path, ".git"));
        var result = await GitAsync(path, ["rev-parse", "--show-toplevel"], ct);
        if (result.ExitCode != 0 || !string.Equals(Path.GetFullPath(result.Stdout.Trim()), Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("The selected directory must be the repository root.");
        var head = await GitAsync(path, ["rev-parse", "--verify", "HEAD"], ct);
        if (head.ExitCode != 0 || head.Stdout.Trim().Length != 40 || !head.Stdout.Trim().All(char.IsAsciiHexDigit)) throw new ArgumentException("A committed SHA-1 Git repository is required.");
        return head.Stdout.Trim();
    }
    public async Task<RepositorySnapshot> SnapshotAsync(RepositoryProject project, CancellationToken ct)
    {
        var head = await ValidateRepositoryAsync(project.RepositoryPath, ct);
        var tree = await GitAsync(project.RepositoryPath, ["ls-tree", "-rz", "--full-tree", head], ct);
        if (tree.ExitCode != 0) throw new ExecutionBlockedException("REPOSITORY_INVENTORY_FAILED");
        var files = new List<RepositoryFile>(); var omitted = new List<string>(); var size = 0;
        var strictUtf8 = new UTF8Encoding(false, true);
        foreach (var entry in tree.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = entry.IndexOf('\t'); if (at < 0) throw new InvalidDataException("Invalid Git inventory.");
            var metadata = entry[..at].Split(' '); var path = entry[(at + 1)..];
            if (metadata[0] is not ("100644" or "100755") || !SoftwareContracts.Matches(path, project.ReadPaths)) { omitted.Add(path); continue; }
            var measured = await GitAsync(project.RepositoryPath, ["cat-file", "-s", metadata[2]], ct);
            if (measured.ExitCode != 0 || !long.TryParse(measured.Stdout.Trim(), out var blobBytes) || blobBytes > 256 * 1024) { omitted.Add(path); continue; }
            var blob = await GitAsync(project.RepositoryPath, ["cat-file", "blob", metadata[2]], ct);
            if (blob.ExitCode != 0 || blob.StdoutBytes.Length > 256 * 1024) { omitted.Add(path); continue; }
            string text;
            try { text = strictUtf8.GetString(blob.StdoutBytes); } catch (DecoderFallbackException) { omitted.Add(path); continue; }
            if (text.Contains('\0') || SourceSafety.ContainsKnownSecret(text)) { omitted.Add(path); continue; }
            size += blob.StdoutBytes.Length;
            if (size > 2 * 1024 * 1024 || files.Count >= 2000) throw new ExecutionBlockedException("REPOSITORY_READ_BUDGET_EXCEEDED");
            files.Add(new(path, text, RuntimeJson.TextHash(text), metadata[0]));
        }
        if (files.Count == 0) throw new ArgumentException("No safe UTF-8 tracked files match this project's read scope.");
        if (files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count) throw new WorkspaceConflictException("Case-only repository paths are not portable across supported systems.");
        return new(head, files.ToArray(), omitted.ToArray(), RuntimeJson.Hash(new { head, files, omitted }));
    }
    public static bool TouchesOmitted(string path, string[] omitted) => omitted.Any(p => p.Equals(path, StringComparison.OrdinalIgnoreCase) || p.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase) || path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase));
    public static RepositoryFile[] Apply(RepositoryFile[] baseline, RepositoryPatch patch, string[] allowedPaths)
    {
        if (patch is null || string.IsNullOrWhiteSpace(patch.Summary) || patch.Summary.Length > 2000 || patch.Files is not { Length: > 0 and <= 24 } || patch.Files.Any(f => f is null ||
            !SoftwareContracts.Matches(f.Path, allowedPaths) || f.Content is null || Encoding.UTF8.GetByteCount(f.Content) > 128 * 1024 || f.Content.Contains('\0') || SourceSafety.ContainsKnownSecret(f.Content)) ||
            patch.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != patch.Files.Length || patch.Files.Sum(f => Encoding.UTF8.GetByteCount(f.Content)) > 512 * 1024 || SourceSafety.ContainsKnownSecret(patch.Summary))
            throw new ArgumentException("Patch must contain 1–24 unique allowed text paths, exact before hashes, at most 128 KiB/file and 512 KiB total, without known secrets.");
        var result = baseline.ToDictionary(f => f.Path, StringComparer.Ordinal);
        foreach (var file in patch.Files)
        {
            var prior = result.GetValueOrDefault(file.Path);
            if (prior?.Sha256 != file.BeforeSha256) throw new WorkspaceConflictException("Patch base differs for " + file.Path);
            if (result.Keys.Any(p => p != file.Path && p.Equals(file.Path, StringComparison.OrdinalIgnoreCase))) throw new WorkspaceConflictException("Case-only path collisions are not portable.");
            if (result.Keys.Any(p => p.StartsWith(file.Path + "/", StringComparison.Ordinal) || file.Path.StartsWith(p + "/", StringComparison.Ordinal))) throw new WorkspaceConflictException("A file/directory path collision is not permitted.");
            result[file.Path] = new(file.Path, file.Content, RuntimeJson.TextHash(file.Content), prior?.Mode ?? "100644");
        }
        return result.Values.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
    }
    public async Task<string> MaterializeWorktreeAsync(SoftwareRun run, SoftwareJob job, RepositoryFile[] files, CancellationToken ct)
    {
        Identifiers.Require(run.Seed.CompanyId); Identifiers.Require(run.Seed.RunId); Identifiers.Require(job.JobId);
        var parent = LocalFiles.Child(_root, "worktrees", run.Seed.CompanyId, run.Seed.RunId); Directory.CreateDirectory(parent); NoLinks(parent);
        var directory = LocalFiles.Child(parent, job.JobId); var marker = LocalFiles.Child(parent, job.JobId + ".json");
        var fingerprint = RuntimeJson.Hash(new { run.Seed.Repository.BaseCommit, files });
        if (Directory.Exists(directory))
        {
            NoLinks(directory);
            if (!File.Exists(marker) || await File.ReadAllTextAsync(marker, ct) != fingerprint) throw new WorkspaceConflictException("Managed worktree is incomplete; reconciliation is required.");
            foreach (var file in files)
            {
                var path = SafeWorktreePath(directory, file.Path);
                if (!File.Exists(path) || RuntimeJson.TextHash(await File.ReadAllTextAsync(path, ct)) != file.Sha256) throw new WorkspaceConflictException("Managed worktree changed outside its recorded operation.");
            }
            return directory;
        }
        var created = await GitAsync(run.Seed.Project.RepositoryPath, ["worktree", "add", "--detach", "--no-checkout", directory, run.Seed.Repository.BaseCommit], ct);
        if (created.ExitCode != 0) throw new ExecutionBlockedException("WORKTREE_CREATE_FAILED");
        var indexed = await GitAsync(directory, ["read-tree", run.Seed.Repository.BaseCommit], ct);
        if (indexed.ExitCode != 0) throw new ExecutionBlockedException("WORKTREE_INDEX_FAILED");
        foreach (var file in files)
        {
            var path = SafeWorktreePath(directory, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); NoLinks(path);
            await LocalFiles.WriteAtomicAsync(path, Encoding.UTF8.GetBytes(file.Content), ct);
            if (!OperatingSystem.IsWindows() && file.Mode == "100755") File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        await LocalFiles.WriteAtomicAsync(marker, Encoding.UTF8.GetBytes(fingerprint), ct); return directory;
    }
    public static string SafeWorktreePath(string root, string relative)
    {
        if (!SoftwareContracts.SafeRelative(relative) || SoftwareContracts.SecretPath(relative)) throw new ArgumentException("Denied worktree path.");
        var result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new ArgumentException("Path escapes worktree.");
        NoLinks(result); return result;
    }
    public async Task<ReleaseOutcome> CommitAsync(SoftwareRun run, CancellationToken ct)
    {
        if (run.Candidate is null || run.Report.Status != "ready_for_owner") throw new WorkspaceConflictException("A passing candidate is required.");
        var syntheticJob = new SoftwareJob("release-" + run.Seed.RunId, run.Seed.RunId, run.Seed.CompanyId, "release", "subpm", "owner", run.Round, null, "", "");
        var directory = await MaterializeWorktreeAsync(run, syntheticJob, run.Candidate.Files, ct);
        var baselineIndex = await GitAsync(directory, ["read-tree", run.Seed.Repository.BaseCommit], ct);
        if (baselineIndex.ExitCode != 0) throw new ExecutionBlockedException("RELEASE_BASE_INDEX_FAILED");
        foreach (var path in run.Candidate.ChangedPaths)
        {
            var file = run.Candidate.Files.Single(f => f.Path == path);
            // Plumbing stages exact accepted bytes, bypassing .gitattributes filters and line-ending conversion.
            var blob = await GitAsync(directory, ["hash-object", "-w", "--no-filters", "--stdin"], ct, Encoding.UTF8.GetBytes(file.Content));
            if (blob.ExitCode != 0) throw new ExecutionBlockedException("GIT_BLOB_FAILED");
            var stage = await GitAsync(directory, ["update-index", "--add", "--cacheinfo", file.Mode + "," + blob.Stdout.Trim() + "," + path], ct);
            if (stage.ExitCode != 0) throw new ExecutionBlockedException("GIT_STAGE_FAILED");
        }
        var tree = await GitAsync(directory, ["write-tree"], ct); if (tree.ExitCode != 0) throw new ExecutionBlockedException("GIT_TREE_FAILED");
        var manifest = RuntimeJson.Hash(new { run.Candidate.Sha256, tree = tree.Stdout.Trim(), parent = run.Seed.Repository.BaseCommit });
        // commit-tree avoids hooks, signing agents and checkout mutations. The source working directory is never updated.
        var environment = new Dictionary<string, string> { ["GIT_AUTHOR_NAME"] = "AI Company OS", ["GIT_AUTHOR_EMAIL"] = "local-owner@localhost", ["GIT_COMMITTER_NAME"] = "AI Company OS", ["GIT_COMMITTER_EMAIL"] = "local-owner@localhost" };
        var committed = await GitAsync(directory, ["commit-tree", tree.Stdout.Trim(), "-p", run.Seed.Repository.BaseCommit, "-m", "AI Company delivery " + run.Seed.RunId + "\n\nCandidate: " + run.Candidate.Sha256], ct, environment: environment);
        if (committed.ExitCode != 0) throw new ExecutionBlockedException("GIT_COMMIT_FAILED");
        var sha = committed.Stdout.Trim(); var branch = "aicos/" + run.Seed.CompanyId + "/" + run.Seed.RunId;
        var updated = await GitAsync(directory, ["update-ref", "refs/heads/" + branch, sha, new string('0', 40)], ct);
        if (updated.ExitCode != 0)
        {
            var prior = await GitAsync(directory, ["rev-parse", "--verify", "refs/heads/" + branch], ct);
            if (prior.ExitCode != 0) throw new ExecutionBlockedException("RELEASE_REF_CONFLICT");
            var priorTree = await GitAsync(directory, ["rev-parse", prior.Stdout.Trim() + "^{tree}"], ct);
            var priorParents = await GitAsync(directory, ["rev-list", "--parents", "-n", "1", prior.Stdout.Trim()], ct);
            if (priorTree.Stdout.Trim() != tree.Stdout.Trim() || priorParents.Stdout.Trim().Split(' ') is not [_, var parentCommit] || parentCommit != run.Seed.Repository.BaseCommit) throw new WorkspaceConflictException("Scoped release branch already contains different content or ancestry.");
            sha = prior.Stdout.Trim();
        }
        return new(sha, branch, false, null, manifest);
    }
}
