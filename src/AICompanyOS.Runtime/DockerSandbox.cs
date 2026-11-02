using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AICompanyOS.Runtime;

public sealed record SandboxCapability(bool Available, string? ImageId, string? EngineVersion, string Reason);

public sealed class DockerSandbox
{
    private readonly string _root;
    private readonly string _endpoint;
    private readonly TrustedProcess _process;
    public DockerSandbox(string dataRoot, string? endpoint = null)
    {
        _root = LocalFiles.Child(Path.GetFullPath(dataRoot), "execution", "docker-client"); Directory.CreateDirectory(_root);
        _process = new(_root);
        _endpoint = endpoint ?? Environment.GetEnvironmentVariable("AICOS_DOCKER_ENDPOINT") ?? (OperatingSystem.IsWindows() ? "npipe:////./pipe/docker_engine" : "unix:///var/run/docker.sock");
        if (!(_endpoint.StartsWith("unix:///", StringComparison.Ordinal) && Uri.TryCreate(_endpoint, UriKind.Absolute, out var uri) && uri.Host.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.AbsolutePath.EndsWith(".sock", StringComparison.Ordinal)) &&
            _endpoint != "npipe:////./pipe/docker_engine") throw new ArgumentException("Docker must use an explicitly local Unix socket or the local Windows Docker named pipe.");
    }
    private Task<ProcessCapture> DockerAsync(string[] args, int timeout, CancellationToken ct, byte[]? input = null, int limit = 65536) =>
        _process.RunAsync("docker", ["--host", _endpoint, .. args], _root, timeout, ct, input, outputLimit: limit);
    public async Task<SandboxCapability> ProbeAsync(string image, CancellationToken ct)
    {
        if (image is not { Length: > 0 and <= 160 } || !char.IsAsciiLetterOrDigit(image[0]) || !image.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '/' or ':' or '@')) throw new ArgumentException("Invalid local image reference.");
        try
        {
            var info = await DockerAsync(["info", "--format", "{{json .}}"], 10, ct);
            if (info.ExitCode != 0) return new(false, null, null, "DOCKER_ENGINE_UNAVAILABLE");
            using var doc = JsonDocument.Parse(info.Stdout);
            if (doc.RootElement.GetProperty("OSType").GetString() != "linux") return new(false, null, null, "LINUX_CONTAINERS_REQUIRED");
            var version = doc.RootElement.GetProperty("ServerVersion").GetString();
            var inspected = await DockerAsync(["image", "inspect", image], 10, ct);
            if (inspected.ExitCode != 0) return new(false, null, version, "IMAGE_NOT_INSTALLED_NO_AUTO_PULL");
            using var metadata = JsonDocument.Parse(inspected.Stdout);
            var item = metadata.RootElement[0];
            if (item.GetProperty("Os").GetString() != "linux" || item.GetProperty("Config").TryGetProperty("Volumes", out var volumes) && volumes.ValueKind == JsonValueKind.Object && volumes.EnumerateObject().Any())
                return new(false, null, version, "IMAGE_WITH_PERSISTENT_VOLUMES_DENIED");
            var id = item.GetProperty("Id").GetString();
            if (id is not { Length: 71 } || !id.StartsWith("sha256:", StringComparison.Ordinal) || !id[7..].All(char.IsAsciiHexDigit)) return new(false, null, version, "IMAGE_DIGEST_INVALID");
            return new(true, id, version, "Local Linux image pinned; no image download or host mounts.");
        }
        catch (Exception e) when (e is ExecutionBlockedException or JsonException or InvalidOperationException or KeyNotFoundException or OperationCanceledException && !ct.IsCancellationRequested)
        { return new(false, null, null, "SANDBOX_UNAVAILABLE"); }
    }
    public static byte[] Archive(RepositoryFile[] files)
    {
        using var stream = new MemoryStream();
        using (var tar = new TarWriter(stream, TarEntryFormat.Pax, true))
        {
            foreach (var file in files)
            {
                if (!SoftwareContracts.SafeRelative(file.Path) || SoftwareContracts.SecretPath(file.Path) || file.Sha256 != RuntimeJson.TextHash(file.Content)) throw new ArgumentException("Invalid sandbox snapshot.");
                using var content = new MemoryStream(Encoding.UTF8.GetBytes(file.Content));
                var entry = new PaxTarEntry(TarEntryType.RegularFile, file.Path) { DataStream = content, Uid = 65532, Gid = 65532,
                    Mode = file.Mode == "100755" ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute : UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    ModificationTime = DateTimeOffset.UnixEpoch };
                tar.WriteEntry(entry);
            }
        }
        return stream.ToArray();
    }
    public async Task<ExecutionReceipt> ExecuteAsync(SoftwareRun run, SoftwareJob job, CommandSpec command, RepositoryFile[] files, string imageId, CancellationToken ct)
    {
        SoftwareContracts.ValidateCommand(command);
        var capability = await ProbeAsync(imageId, ct);
        if (!capability.Available || capability.ImageId != imageId) return SoftwareChecks.NotRun(run, job, command, capability.Reason);
        var input = Archive(files); var archiveHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(input));
        var name = "aicos-" + job.JobId;
        Identifiers.Require(name);
        var started = DateTimeOffset.UtcNow;
        ProcessCapture? output = null; var status = "not_run"; var findings = new List<RuntimeFinding>();
        try
        {
            var created = await DockerAsync(["create", "--name", name, "--label", "ai-company-os.managed=true", "--label", "ai-company-os.job=" + job.JobId,
                "--pull=never", "--network=none", "--read-only", "--cap-drop=ALL", "--security-opt=no-new-privileges=true", "--pids-limit=128", "--memory=1g", "--memory-swap=1g", "--cpus=2",
                "--ulimit", "nofile=1024:1024", "--ulimit", "core=0", "--log-driver=none", "--user=65532:65532", "--init", "--interactive", "--workdir=/work",
                "--tmpfs", "/work:rw,exec,nosuid,nodev,mode=1777,size=268435456", "--tmpfs", "/tmp:rw,exec,nosuid,nodev,mode=1777,size=268435456",
                "--env", "HOME=/tmp", "--env", "DOTNET_CLI_HOME=/tmp", "--env", "DOTNET_CLI_TELEMETRY_OPTOUT=1", "--env", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1",
                "--env", "PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin", "--entrypoint=/bin/sh", imageId,
                "-c", "umask 077; tar -xf - -C /work && exec \"$@\"", "aicos", command.Executable, .. command.Arguments], 15, ct);
            if (created.ExitCode != 0) throw new ExecutionBlockedException("CONTAINER_CREATE_FAILED");
            output = await DockerAsync(["start", "--attach", "--interactive", name], command.TimeoutSeconds, ct, input, limit: 65536);
            var inspected = await DockerAsync(["inspect", "--format", "{{json .State}}", name], 10, ct);
            if (inspected.ExitCode != 0) throw new ExecutionBlockedException("CONTAINER_RESULT_UNVERIFIABLE");
            using var state = JsonDocument.Parse(inspected.Stdout);
            if (state.RootElement.GetProperty("Running").GetBoolean()) throw new ExecutionBlockedException("CONTAINER_STILL_RUNNING");
            var exit = state.RootElement.GetProperty("ExitCode").GetInt32();
            if (exit != output.ExitCode || state.RootElement.GetProperty("OOMKilled").GetBoolean()) throw new ExecutionBlockedException("CONTAINER_RESULT_MISMATCH_OR_OOM");
            status = exit == 0 ? "executed" : "failed";
            if (exit != 0) findings.Add(new("command-failed", "high", command.Name + " exited " + exit));
        }
        catch (OperationCanceledException) { status = ct.IsCancellationRequested ? "cancelled" : "blocked"; findings.Add(new("cancelled-or-timeout", "high", "The container request was cancelled or exceeded its time limit.")); }
        catch (ExecutionBlockedException error) { status = "blocked"; findings.Add(new("sandbox-blocked", "high", error.Message)); }
        catch (Exception) { status = "blocked"; findings.Add(new("sandbox-unverifiable", "high", "Container output/state could not be verified.")); }
        finally
        {
            // Cleanup is restricted to our recorded job name and ownership labels; never touches unrelated containers.
            try
            {
                var identity = await DockerAsync(["inspect", "--format", "{{json .Config.Labels}}", name], 5, CancellationToken.None);
                if (identity.ExitCode == 0)
                {
                    using var labels = JsonDocument.Parse(identity.Stdout);
                    if (labels.RootElement.GetProperty("ai-company-os.job").GetString() != job.JobId || labels.RootElement.GetProperty("ai-company-os.managed").GetString() != "true")
                        throw new ExecutionBlockedException("CONTAINER_OWNERSHIP_MISMATCH");
                    var removed = await DockerAsync(["rm", "--force", "--volumes", name], 10, CancellationToken.None);
                    if (removed.ExitCode != 0) throw new ExecutionBlockedException("CONTAINER_CLEANUP_UNCONFIRMED");
                }
            }
            catch (Exception) { status = "blocked"; findings.Add(new("cleanup-unconfirmed", "critical", "Container cleanup could not be confirmed; inspect the recorded job before continuing.")); }
        }
        var stdout = Redact(output?.Stdout ?? ""); var stderr = Redact(output?.Stderr ?? "");
        return new("receipt-" + Guid.NewGuid().ToString("N"), job.JobId, job.RoleId, job.AgentId, "docker." + command.Name, "1.0.0",
            run.Candidate?.Sha256 ?? run.Seed.Repository.Sha256, status, status is "executed" or "failed" ? output?.ExitCode : null, started, DateTimeOffset.UtcNow,
            stdout, stderr, SoftwareChecks.LogHash(stdout, stderr, findings.ToArray(), archiveHash, command), name, imageId, findings.ToArray(),
            run.Plan?.Tasks.Any(t => t.AgentId == job.AgentId) == true, command, archiveHash, job.Attempts);
    }
    public async Task ReconcileAsync(IEnumerable<SoftwareJob> orphanJobs, CancellationToken ct)
    {
        foreach (var job in orphanJobs)
        {
            var name = "aicos-" + job.JobId;
            try
            {
                var identity = await DockerAsync(["inspect", "--format", "{{json .Config.Labels}}", name], 5, ct);
                if (identity.ExitCode != 0) continue;
                using var labels = JsonDocument.Parse(identity.Stdout);
                if (labels.RootElement.GetProperty("ai-company-os.job").GetString() != job.JobId || labels.RootElement.GetProperty("ai-company-os.managed").GetString() != "true") throw new ExecutionBlockedException("ORPHAN_CONTAINER_OWNERSHIP_MISMATCH");
                var removed = await DockerAsync(["rm", "--force", "--volumes", name], 10, ct);
                if (removed.ExitCode != 0) throw new ExecutionBlockedException("ORPHAN_CONTAINER_CLEANUP_FAILED");
            }
            catch (ExecutionBlockedException) { throw; }
        }
    }
    private static string Redact(string text) => SourceSafety.ContainsKnownSecret(text) ? "[Output withheld: possible credentials detected.]" : text;
}
