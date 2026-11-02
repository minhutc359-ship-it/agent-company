using System.Diagnostics;
using System.Text.Json;

namespace AICompanyOS.Runtime;

public static class RuntimeBuild
{
    public static JsonElement Info => File.Exists(Path.Combine(AppContext.BaseDirectory, "build-info.json")) ? JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "build-info.json"))) : JsonSerializer.SerializeToElement(new { version = "2.5.0-dev", commit = "source", buildNumber = 0, protocol = 1, runtimeMajor = 10 });
}
public sealed record UpdateApplyRequest(string Tag, string Sha256);

public sealed class UpdateService(string dataRoot)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? Root => Environment.GetEnvironmentVariable("AICOS_INSTALL_ROOT");
    private string? Prefix => Environment.GetEnvironmentVariable("AICOS_INSTALL_PREFIX");
    public object Status()
    {
        var path = Prefix is null ? null : Path.Combine(Prefix, "updates", "state.json");
        return new { managed = Root is not null && Prefix is not null, build = RuntimeBuild.Info, state = path is not null && File.Exists(path) ? JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path)) : JsonSerializer.SerializeToElement(new { phase = "idle" }), detail = Root is null ? "Mở bằng Start-Windows.cmd hoặc python setup/setup-aicos.py --source --launch để bật nâng cấp tự động." : "Nâng cấp giữ nguyên DB, cấu hình, project và model; API key RAM cần nhập lại sau khởi động." };
    }
    public async Task<JsonElement> RunAsync(string action, CancellationToken ct)
    {
        if (Root is null || Prefix is null) throw new WorkspaceConflictException("Mở ứng dụng bằng launcher để dùng cập nhật tự động.");
        await _gate.WaitAsync(ct);
        try
        {
            var python = Environment.GetEnvironmentVariable("AICOS_PYTHON") ?? throw new WorkspaceConflictException("Thiếu Python của launcher.");
            var start = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var arg in new[] { Path.Combine(Root, "update_engine.py"), action, "--root", Root, "--prefix", Prefix }) start.ArgumentList.Add(arg);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(3));
            using var process = Process.Start(start) ?? throw new IOException("Không khởi động được updater.");
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                await Task.WhenAll(output, errors, process.WaitForExitAsync(timeout.Token));
                using var result = JsonDocument.Parse(await output);
                if (process.ExitCode != 0) throw new WorkspaceConflictException(result.RootElement.TryGetProperty("error", out var e) ? e.GetString()! : "Không kiểm tra/tải được cập nhật.");
                return result.RootElement.Clone();
            }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } }
        }
        finally { _gate.Release(); }
    }
    public async Task ApplyAsync(UpdateApplyRequest request, FileEventStore store, IHostApplicationLifetime lifetime, CancellationToken ct)
    {
        if (Root is null || Prefix is null || Path.GetFullPath(dataRoot) != Path.GetFullPath(Path.Combine(Prefix, "data"))) throw new WorkspaceConflictException("Dữ liệu chưa thuộc launcher; mở lại bằng launcher trước khi nâng cấp.");
        await _gate.WaitAsync(ct);
        await store.SetMaintenanceAsync(true, ct);
        try
        {
            foreach (var company in await store.ListCompaniesAsync(ct))
            {
                var wb = await store.WorkbenchAsync(company.CompanyId, ct);
                if (wb.Runs.Any(r => r.Jobs.Any(j => j.Status is "running" or "queued") && r.State is not ("cancelled" or "completed" or "paused")) || wb.Consultations.Any(j => j.Status is "queued" or "running") || wb.EgressManifests.Any(m => m.Status is "queued" or "running") || wb.TeamDiscussions.Any(d => d.Status == "active")) throw new WorkspaceConflictException("Còn agent đang làm việc. Tạm dừng công việc, dừng hội ý và chờ lượt AI hiện tại kết thúc trước khi nâng cấp.");
                var workspace = await store.WorkspaceSnapshotAsync(company.CompanyId, ct);
                if (workspace.Runs.Any(r => r.Invocations?.Any(j => j.Status is "queued" or "running") == true)) throw new WorkspaceConflictException("Luồng tĩnh còn AI đang chạy; chờ hoàn tất trước khi nâng cấp.");
            }
            var state = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(Prefix, "updates", "state.json")));
            if (state.GetProperty("phase").GetString() != "ready") throw new WorkspaceConflictException("Hãy tải và xác minh gói mới trước.");
            if (state.GetProperty("tag").GetString() != request.Tag || state.GetProperty("sha256").GetString() != request.Sha256) throw new WorkspaceConflictException("Gói cập nhật đã thay đổi. Mở lại cửa sổ cập nhật để đối chiếu bản vừa tải.");
            await LocalFiles.WriteAtomicAsync(Path.Combine(Prefix, "updates", "apply-request.json"), JsonSerializer.SerializeToUtf8Bytes(new { requestedAt = DateTimeOffset.UtcNow }), ct);
            _ = Task.Run(async () => { await Task.Delay(600); Environment.ExitCode = 42; lifetime.StopApplication(); });
        }
        catch { await store.SetMaintenanceAsync(false, CancellationToken.None); throw; }
        finally { _gate.Release(); }
    }
}
