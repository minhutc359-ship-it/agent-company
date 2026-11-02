using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;

namespace AICompanyOS.Runtime;

public static class SoftwareEndpoints
{
    public static void Map(RouteGroupBuilder companies, RouteGroupBuilder api)
    {
        api.MapGet("/updates/status", (UpdateService updates) => updates.Status());
        api.MapPost("/agent-models", async (AgentModelRequest request, CancellationToken ct) =>
        {
            var policy = CloudProviderPolicy.Resolve(request.ProviderType);
            if (string.IsNullOrWhiteSpace(request.Secret) || request.Secret.Length > 8192 || request.Secret.Any(char.IsControl)) throw new ArgumentException("Nhập API key hợp lệ.");
            return await new CloudModelCatalog().CheckAsync(new("preview", 1, request.ProviderType, "not-selected", policy.Host, policy.Path, policy.Version, "memory", true, policy.Caveat, DateTimeOffset.UtcNow), request.Secret, ct);
        });
        api.MapPost("/updates/check", (UpdateService updates, CancellationToken ct) => updates.RunAsync("check", ct));
        api.MapPost("/updates/download", (UpdateService updates, CancellationToken ct) => updates.RunAsync("stage", ct));
        api.MapPost("/updates/apply", async (UpdateApplyRequest request, UpdateService updates, FileEventStore store, IHostApplicationLifetime lifetime, CancellationToken ct) => { await updates.ApplyAsync(request, store, lifetime, ct); return Results.Ok(new { restarting = true }); });
        companies.MapGet("/agent-connections", async (string companyId, FileEventStore store, RuntimeCredentialVault vault, CancellationToken ct) =>
        {
            var wb = await store.WorkbenchAsync(companyId, ct);
            return wb.AgentConfigurations.Select(c => new { c.AgentId, c.ModelId, c.ProviderId, c.Engine, keyAvailable = c.ProviderId is null || wb.CloudProviders.Any(p => p.ProviderId == c.ProviderId && vault.Status(companyId, p.ProviderId, p.ProviderType, p.CredentialRef).Available) });
        });
        companies.MapPost("/agents/{agentId}/connect", async (string companyId, string agentId, AgentConnectRequest request, FileEventStore store, RuntimeCredentialVault vault, CancellationToken ct) =>
        {
            if (!request.AllowCloud) throw new ArgumentException("Bạn cần đồng ý cho gửi ngữ cảnh công việc tới dịch vụ AI đã chọn.");
            if (!CloudProviderPolicy.ValidModel(request.ModelId) || request.Engine is not ("api" or "claude-code") || string.IsNullOrWhiteSpace(request.Secret) || request.Secret.Length > 8192 || request.Secret.Any(char.IsControl)) throw new ArgumentException("Chọn model, engine và API key hợp lệ.");
            var policy = CloudProviderPolicy.Resolve(request.ProviderType);
            var wb = await store.WorkbenchAsync(companyId, ct); var c = wb.AgentConfigurations.FirstOrDefault(c => c.AgentId == agentId);
            if ((c?.Version ?? 0) != request.ExpectedVersion) throw new WorkspaceConflictException("Cấu hình agent đã thay đổi, mở lại form kết nối.");
            var providerId = "agent-route-" + RuntimeJson.TextHash(agentId)[7..23]; var prior = wb.CloudProviders.FirstOrDefault(p => p.ProviderId == providerId);
            if (request.Engine == "claude-code") _ = ClaudeCodeClient.Route(new(providerId, 1, request.ProviderType, request.ModelId, policy.Host, policy.Path, policy.Version, "memory", policy.Caveat));
            var p = prior is { Enabled: true, CredentialRef: "memory" } && prior.ProviderType == request.ProviderType && prior.ModelId == request.ModelId ? prior : await store.ConfigureCloudProviderAsync(companyId, providerId, new(prior?.Version ?? 0, request.ProviderType, request.ModelId), ct);
            if (wb.Profile.PrivacyMode != "controlled_cloud") await store.ConfigureCompanyAsync(companyId, new(wb.Profile.Version, wb.Profile.Description, wb.Profile.Departments, wb.Profile.AllowGitHubRelease, "controlled_cloud", wb.Profile.CloudPolicyVersion, wb.Profile.MaxCloudOutputTokens), ct);
            var configured = c is not null && c.ProviderId == p.ProviderId && c.ModelId == request.ModelId && c.Engine == request.Engine ? c : await store.ConfigureAgentAsync(companyId, agentId, new(c?.Version ?? 0, c?.Department ?? wb.Profile.Departments[0], request.ModelId, c?.SkillRefs ?? [], c?.MaxInputBytes ?? 10000, Math.Min(c?.MaxOutputTokens ?? 2048, wb.Profile.MaxCloudOutputTokens), p.ProviderId, request.Engine), ct);
            vault.Set(companyId, p.ProviderId, p.ProviderType, request.Secret);
            return new { configured.AgentId, configured.ModelId, configured.ProviderId, configured.Engine, keyAvailable = true, detail = "Đã kết nối. Run mới dùng cấu hình này; từng nội dung gửi cloud cần bạn duyệt. Key chỉ giữ trong phiên hiện tại." };
        });
        companies.MapGet("/software-runs/{runId}/jobs/{jobId}/cloud-preview", (string companyId, string runId, string jobId, FileEventStore store, CancellationToken ct) => store.PreviewSoftwareCloudAsync(companyId, runId, jobId, ct));
        companies.MapPost("/software-runs/{runId}/jobs/{jobId}/cloud-approve", async (string companyId, string runId, string jobId, CloudJobDecision request, FileEventStore store, RuntimeCredentialVault vault, CancellationToken ct) =>
        {
            var run = await store.GetSoftwareRunAsync(companyId, runId, ct) ?? throw new KeyNotFoundException(); var job = run.Jobs.Single(j => j.JobId == jobId);
            var p = run.Seed.Bindings.Single(b => b.RoleId == job.RoleId && b.Agent.AgentId == job.AgentId).Cloud?.Provider ?? throw new ArgumentException("Node không dùng cloud.");
            if (!vault.Status(companyId, p.ProviderId, p.ProviderType, p.CredentialRef).Available) throw new WorkspaceConflictException("API key đã hết phiên. Bấm Kết nối AI để nhập lại key của nhân viên.");
            return await store.ApproveSoftwareCloudAsync(companyId, runId, jobId, request, ct);
        });
        api.MapGet("/execution/capabilities", async (string? image, DockerSandbox sandbox, CancellationToken ct) => await sandbox.ProbeAsync(image ?? "node:22-alpine", ct));
        api.MapPost("/commands/resolve", (CompanyCommandRequest request) => CompanyCommandParser.Resolve(request.Text));
        api.MapPost("/workflows/validate", (SoftwareWorkflow workflow) => SoftwareContracts.Compile(workflow));
        api.MapGet("/company-starters", () => new[] { CompanyStarters.SoftwareTeam });
        api.MapGet("/setup/status", async (LocalSetupService setup, FileEventStore store, ISoftwareModelClient models, DockerSandbox sandbox, CancellationToken ct) => await setup.CheckAsync(store, models, sandbox, ct));
        api.MapPost("/setup/demo", async (DemoSetupRequest request, LocalSetupService setup, FileEventStore store, RepositoryService repositories, ISoftwareModelClient models, CancellationToken ct) =>
            await setup.InstallDemoAsync(store, repositories, request.ModelId, request.ModelId is null ? [] : await models.ListAsync(ct), ct));
        companies.MapPost("/team-discussions", async (string companyId, TeamDiscussionRequest request, HttpContext context, FileEventStore store, ISoftwareModelClient models, CancellationToken ct) => await store.StartTeamDiscussionAsync(companyId, request, await models.ListAsync(ct), Key(context), ct));
        companies.MapPost("/team-discussions/{discussionId}/cancel", async (string companyId, string discussionId, FileEventStore store, CancellationToken ct) => await store.CancelTeamDiscussionAsync(companyId, discussionId, ct));
        companies.MapGet("/software-runs/{runId}/external-handoff/{nodeId}", async Task<IResult> (string companyId, string runId, string nodeId, FileEventStore store, CancellationToken ct) =>
        { var run = await store.GetSoftwareRunAsync(companyId, runId, ct); return run is null ? Results.NotFound() : Results.File(LocalSetupService.ExportHandoff(run, nodeId), "application/zip", "agent-handoff.zip"); });
        companies.MapPost("/starter-pack", async (string companyId, StarterInstallRequest request, HttpContext context, FileEventStore store, ISoftwareModelClient models, CancellationToken ct) =>
            await store.InstallCompanyStarterAsync(companyId, request, Key(context), request.ModelId is null ? [] : await models.ListAsync(ct), ct));
        companies.MapGet("/workbench", async (string companyId, FileEventStore store, CancellationToken ct) => await store.WorkbenchAsync(companyId, ct));
        companies.MapPut("/profile", async (string companyId, CompanyProfileRequest request, FileEventStore store, CancellationToken ct) => await store.ConfigureCompanyAsync(companyId, request, ct));
        companies.MapPost("/projects", async (string companyId, ProjectRequest request, FileEventStore store, RepositoryService repositories, CancellationToken ct) => await store.ConfigureProjectAsync(companyId, request, repositories, ct));
        companies.MapGet("/projects/{projectId}/inventory", async Task<IResult> (string companyId, string projectId, FileEventStore store, RepositoryService repositories, CancellationToken ct) =>
        {
            var project = (await store.WorkbenchAsync(companyId, ct)).Projects.FirstOrDefault(p => p.ProjectId == projectId); if (project is null) return Results.NotFound();
            var snapshot = await repositories.SnapshotAsync(project, ct); var dependencies = SoftwareChecks.Dependencies(snapshot.Files);
            return Results.Ok(new { snapshot.BaseCommit, snapshot.Sha256, files = snapshot.Files.Select(f => new { f.Path, f.Sha256 }), snapshot.OmittedPaths, dependencies, dependencyInventorySha256 = RuntimeJson.Hash(dependencies) });
        });
        companies.MapPut("/agents/{agentId}/configuration", async (string companyId, string agentId, AgentConfigurationRequest request, FileEventStore store, CancellationToken ct) => await store.ConfigureAgentAsync(companyId, agentId, request, ct));
        companies.MapPut("/agents/{agentId}/identity", async (string companyId, string agentId, AgentIdentityRequest request, FileEventStore store, CancellationToken ct) => await store.UpdateAgentIdentityAsync(companyId, agentId, request, ct));
        companies.MapPost("/skills", async (string companyId, SkillPackage request, FileEventStore store, CancellationToken ct) => await store.InstallSkillAsync(companyId, request, ct));
        companies.MapPost("/workflows", async (string companyId, SoftwareWorkflow request, FileEventStore store, CancellationToken ct) => await store.SaveWorkflowAsync(companyId, request, false, ct));
        companies.MapPost("/interactions", async (string companyId, InteractionRequest request, HttpContext context, FileEventStore store, CancellationToken ct) => await store.AddInteractionAsync(companyId, request, Key(context), ct));
        companies.MapPost("/conversation-branches", async (string companyId, ConversationBranchRequest request, HttpContext context, FileEventStore store, CancellationToken ct) => await store.CreateConversationBranchAsync(companyId, request, Key(context), ct));
        companies.MapGet("/consultations", async (string companyId, FileEventStore store, CancellationToken ct) => await store.ListConsultationsAsync(companyId, ct));
        companies.MapPost("/interactions/{interactionId}/respond", async (string companyId, string interactionId, ConsultationRequest request, HttpContext context, FileEventStore store, ISoftwareModelClient models, CancellationToken ct) =>
            await store.QueueConsultationAsync(companyId, interactionId, request, await models.ListAsync(ct), Key(context), ct));
        companies.MapPost("/interactions/{interactionId}/compare", async (string companyId, string interactionId, ConsultationComparisonRequest request, HttpContext context, FileEventStore store, ISoftwareModelClient models, CancellationToken ct) =>
            await store.QueueComparisonAsync(companyId, interactionId, request, await models.ListAsync(ct), Key(context), ct));
        companies.MapPost("/consultations/{jobId}/retry", async (string companyId, string jobId, FileEventStore store, CancellationToken ct) => await store.RetryConsultationAsync(companyId, jobId, ct));
        companies.MapPut("/cloud/providers/{providerId}", async (string companyId, string providerId, CloudProviderConfigurationRequest request, FileEventStore store, CancellationToken ct) =>
            await store.ConfigureCloudProviderAsync(companyId, providerId, request, ct));
        companies.MapPost("/cloud/providers/{providerId}/check", async (string companyId, string providerId, FileEventStore store, RuntimeCredentialVault vault, CancellationToken ct) =>
        { var workbench = await store.WorkbenchAsync(companyId, ct); if (workbench.Profile.PrivacyMode != "controlled_cloud") throw new WorkspaceConflictException("Bật Controlled Cloud trước khi kiểm tra provider."); var provider = workbench.CloudProviders.FirstOrDefault(p => p.ProviderId == providerId && p.Enabled) ?? throw new KeyNotFoundException(); return await new CloudModelCatalog().CheckAsync(provider, vault.Resolve(companyId, providerId, provider.ProviderType, provider.CredentialRef), ct); });
        companies.MapPut("/cloud/providers/{providerId}/credential", async (string companyId, string providerId, CloudCredentialRequest request, FileEventStore store, RuntimeCredentialVault vault, CancellationToken ct) =>
        {
            var provider = (await store.WorkbenchAsync(companyId, ct)).CloudProviders.FirstOrDefault(p => p.ProviderId == providerId) ?? throw new KeyNotFoundException();
            return vault.Set(companyId, providerId, provider.ProviderType, request.Secret);
        });
        companies.MapDelete("/cloud/providers/{providerId}/credential", async (string companyId, string providerId, FileEventStore store, RuntimeCredentialVault vault, CancellationToken ct) =>
        {
            var provider = (await store.WorkbenchAsync(companyId, ct)).CloudProviders.FirstOrDefault(p => p.ProviderId == providerId) ?? throw new KeyNotFoundException();
            vault.Clear(companyId, providerId, provider.ProviderType);
            return vault.Status(companyId, providerId, provider.ProviderType, provider.CredentialRef);
        });
        companies.MapGet("/cloud/providers/{providerId}/credential", async Task<IResult> (string companyId, string providerId, FileEventStore store, RuntimeCredentialVault vault, CancellationToken ct) =>
        {
            var provider = (await store.WorkbenchAsync(companyId, ct)).CloudProviders.FirstOrDefault(p => p.ProviderId == providerId);
            return provider is null ? Results.NotFound() : Results.Ok(vault.Status(companyId, providerId, provider.ProviderType, provider.CredentialRef));
        });
        companies.MapPost("/interactions/{interactionId}/cloud-preview", async (string companyId, string interactionId, EgressPreviewRequest request, HttpContext context, FileEventStore store, CancellationToken ct) =>
            await store.PrepareEgressAsync(companyId, interactionId, request, Key(context), ct));
        companies.MapPost("/cloud/egress/{manifestId}/decision", async (string companyId, string manifestId, EgressDecisionRequest request, HttpContext context, FileEventStore store, RuntimeCredentialVault vault, CancellationToken ct) =>
        {
            var provider = (await store.WorkbenchAsync(companyId, ct)).EgressManifests.FirstOrDefault(m => m.ManifestId == manifestId)?.Provider ?? throw new KeyNotFoundException();
            return await store.DecideEgressAsync(companyId, manifestId, request, vault.Status(companyId, provider.ProviderId, provider.ProviderType, provider.CredentialRef).Available, Key(context), ct);
        });
        companies.MapPost("/cloud/egress/{manifestId}/retry", async (string companyId, string manifestId, FileEventStore store, CancellationToken ct) => await store.RetryEgressAsync(companyId, manifestId, ct));
        companies.MapPost("/meetings", async (string companyId, MeetingRequest request, HttpContext context, FileEventStore store, CancellationToken ct) => await store.CreateMeetingAsync(companyId, request, Key(context), ct));
        companies.MapPost("/meetings/{meetingId}/start", async (string companyId, string meetingId, MeetingCompleteRequest request, FileEventStore store, CancellationToken ct) => await store.ChangeMeetingAsync(companyId, meetingId, true, request, ct));
        companies.MapPost("/meetings/{meetingId}/complete", async (string companyId, string meetingId, MeetingCompleteRequest request, FileEventStore store, CancellationToken ct) => await store.ChangeMeetingAsync(companyId, meetingId, false, request, ct));
        companies.MapPost("/meetings/{meetingId}/summarize", async (string companyId, string meetingId, ConsultationRequest request, HttpContext context, FileEventStore store, ISoftwareModelClient models, CancellationToken ct) =>
            await store.QueueConsultationAsync(companyId, "meeting-summary", request with { MeetingId = meetingId }, await models.ListAsync(ct), Key(context), ct));
        companies.MapPost("/software-runs", async (string companyId, SoftwareCreateRequest request, HttpContext context, SoftwareCoordinator coordinator, CancellationToken ct) => await coordinator.CreateAsync(companyId, request, Key(context), ct));
        companies.MapGet("/software-runs/{runId}", async Task<IResult> (string companyId, string runId, FileEventStore store, CancellationToken ct) =>
            await store.GetSoftwareRunAsync(companyId, runId, ct) is { } run ? Results.Ok(run) : Results.NotFound());
        companies.MapGet("/software-runs/{runId}/report", async Task<IResult> (string companyId, string runId, FileEventStore store, CancellationToken ct) =>
            await store.GetSoftwareRunAsync(companyId, runId, ct) is { } run ? Results.Ok(new { run.Report, run.Nodes, run.Jobs, run.Receipts, run.Release }) : Results.NotFound());
        companies.MapPost("/software-runs/{runId}/outputs/{nodeId}", async (string companyId, string runId, string nodeId, SoftwareOutputRequest request, SoftwareCoordinator coordinator, CancellationToken ct) => await coordinator.SupplyOutputAsync(companyId, runId, nodeId, request, ct));
        companies.MapPost("/software-runs/{runId}/jobs/{jobId}/reconcile-worktree", async (string companyId, string runId, string jobId, SoftwareControlRequest request, FileEventStore store, RepositoryService repositories, CancellationToken ct) =>
        {
            var run = await store.GetSoftwareRunAsync(companyId, runId, ct) ?? throw new KeyNotFoundException();
            if (run.State != "blocked" || run.LastSequence != request.ExpectedSequence) throw new WorkspaceConflictException("Reload a blocked recovery checkpoint.");
            var job = run.Jobs.SingleOrDefault(j => j.JobId == jobId) ?? throw new KeyNotFoundException();
            var directory = await repositories.ReconcileWorktreeAsync(run, job, ct);
            return await store.RecordWorktreeReconciliationAsync(companyId, runId, jobId, run.LastSequence, directory, ct);
        });
        companies.MapPost("/software-runs/{runId}/execution-preview", async (string companyId, string runId, SoftwareControlRequest request, SoftwareCoordinator coordinator, CancellationToken ct) => await coordinator.PreviewAsync(companyId, runId, request.ExpectedSequence, ct));
        companies.MapPost("/software-runs/{runId}/plan-approval", async (string companyId, string runId, SoftwareApproval request, HttpContext context, FileEventStore store, CancellationToken ct) => Response(await store.ApproveSoftwarePlanAsync(companyId, runId, request, Key(context), ct)));
        foreach (var action in new[] { "pause", "resume", "step", "cancel", "retry" })
            companies.MapPost("/software-runs/{runId}/" + action, async (string companyId, string runId, SoftwareControlRequest request, HttpContext context, FileEventStore store, CancellationToken ct) => Response(await store.ControlSoftwareAsync(companyId, runId, action, request.ExpectedSequence, Key(context), ct)));
        companies.MapPost("/software-runs/{runId}/revision", async (string companyId, string runId, SoftwareRevisionRequest request, HttpContext context, FileEventStore store, CancellationToken ct) => Response(await store.RequestSoftwareRevisionAsync(companyId, runId, request, Key(context), ct)));
        companies.MapPost("/software-runs/{runId}/final-review", async (string companyId, string runId, SoftwareReleaseRequest request, HttpContext context, FileEventStore store, CancellationToken ct) => Response(await store.ReviewSoftwareDeliveryAsync(companyId, runId, request, Key(context), ct)));
        companies.MapGet("/software-runs/{runId}/release-preview", async Task<IResult> (string companyId, string runId, FileEventStore store, CancellationToken ct) =>
        {
            var workbench = await store.WorkbenchAsync(companyId, ct); var run = workbench.Runs.FirstOrDefault(r => r.Seed.RunId == runId);
            return run is null ? Results.NotFound() : Results.Ok(SoftwareReleaseService.Preview(run, workbench.Profile.AllowGitHubRelease));
        });
        companies.MapPost("/software-runs/{runId}/release", async (string companyId, string runId, SoftwareReleaseRequest request, HttpContext context, SoftwareReleaseService release, CancellationToken ct) => await release.ExecuteAsync(companyId, runId, request, Key(context), ct));
        companies.MapGet("/software-runs/{runId}/export", async Task<IResult> (string companyId, string runId, FileEventStore store, CancellationToken ct) =>
        {
            var run = await store.GetSoftwareRunAsync(companyId, runId, ct); if (run is null) return Results.NotFound();
            if (run.State is not ("completed" or "awaiting_release") || !SoftwareChecks.GatesPass(run) || !run.Nodes.Any(n => n.Id == "final-approval" && n.Status == "approved")) throw new WorkspaceConflictException("Export requires the exact passing, Owner-accepted candidate.");
            using var bytes = new MemoryStream();
            using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            {
                foreach (var file in run.Candidate!.Files.Where(f => run.Candidate.ChangedPaths.Contains(f.Path))) Write("files/" + file.Path, file.Content);
                Write("report.json", JsonSerializer.Serialize(new { run.Report, run.Receipts, run.Candidate.Sha256, run.Release }, RuntimeJson.Options));
                Write("patch.json", JsonSerializer.Serialize(run.Candidate.Patches, RuntimeJson.Options));
                Write("SHA256SUMS.txt", string.Join('\n', run.Candidate.Files.Where(f => run.Candidate.ChangedPaths.Contains(f.Path)).Select(f => f.Sha256[7..] + "  files/" + f.Path)) + "\n");
                void Write(string path, string text) { using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); writer.Write(text); }
            }
            return Results.File(bytes.ToArray(), "application/zip", runId + "-source-delivery.zip");
        });
    }
    private static string Key(HttpContext context)
    {
        var key = context.Request.Headers["Idempotency-Key"].ToString(); if (!Identifiers.IsValid(key)) throw new ArgumentException("A valid Idempotency-Key is required."); return key;
    }
    private static IResult Response(SoftwareCommandResult result) => result.Outcome switch
    {
        CommandOutcome.NotFound => Results.NotFound(),
        CommandOutcome.Conflict => Results.Conflict(new { error = new { code = "SOFTWARE_CONFLICT", message = result.Message } }),
        CommandOutcome.Invalid => Results.UnprocessableEntity(new { error = new { code = "SOFTWARE_INVALID", message = result.Message } }),
        _ => Results.Ok(result.Run)
    };
}
