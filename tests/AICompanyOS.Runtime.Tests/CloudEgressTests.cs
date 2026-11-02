using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AICompanyOS.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

public static class CloudEgressTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static int _checks;
    private static void Check(bool value, string message) { _checks++; if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task> action, string message) where T : Exception { try { await action(); } catch (T) { _checks++; return; } throw new InvalidOperationException(message); }
    public static async Task RunAsync()
    {
        foreach (var backend in new[] { "file", "sqlite" }) await StoreFlowAsync(backend);
        await AdapterContractsAsync();
        Console.WriteLine($"PASS: {_checks} controlled-cloud assertions: strict-local deny, exact payload consent, memory-only credentials, pinned hosts, durable audit and provider response parsing.");
    }
    private static async Task StoreFlowAsync(string backend)
    {
        var root = Path.Combine(Path.GetTempPath(), "aicos-cloud-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var store = new FileEventStore(root, backend); await store.InitializeAsync(Ct);
            var message = await store.AddInteractionAsync("software-studio", new("lobby", "question", "owner", ["ba-agent"], "Review this public delivery question.", [], []), "cloud-question", Ct);
            await Throws<WorkspaceConflictException>(() => store.PrepareEgressAsync("software-studio", message.InteractionId, new("primary", "ba-agent", "ba"), "strict-preview", Ct),
                "Strict Local must block creation of cloud invocation payloads.");
            var route = await store.ConfigureCloudProviderAsync("software-studio", "primary", new(0, "openai", "gpt-6-astra"), Ct);
            Check(route.EndpointHost == "api.openai.com" && route.EndpointPath == "/v1/responses" && route.CredentialRef == "memory", "Provider routes must resolve to a fixed official hostname and credential reference only.");
            var longestProviderId = new string('p', 100);
            await store.ConfigureCloudProviderAsync("software-studio", longestProviderId, new(0, "openai", "gpt-6-astra"), Ct);
            Check((await store.WorkbenchAsync("software-studio", Ct)).CloudProviders.Any(p => p.ProviderId == longestProviderId), "A maximum-length valid provider ID must round-trip without corrupting the event aggregate identity.");
            var profile = await store.ConfigureCompanyAsync("software-studio", new(0, "Cloud test", ["Analysis"], PrivacyMode: "controlled_cloud"), Ct);
            Check((await store.ListCompaniesAsync(Ct)).Single(c => c.CompanyId == "software-studio").PrivacyMode == "controlled_cloud", "Company listings must report the effective company privacy policy.");
            var preview = await store.PrepareEgressAsync("software-studio", message.InteractionId, new("primary", "ba-agent", "ba"), "cloud-preview", Ct);
            Check(preview.Status == "awaiting_approval" && preview.PolicyResult == "needs_approval" && preview.Payload.Contains("\"store\":false") && preview.Provider.Version == route.Version,
                "Controlled Cloud must persist an exact stateless payload and pinned provider configuration before any network call.");
            Check(preview.ContextItems.Any(i => i.Kind == "public_interaction") && preview.PayloadSha256 == RuntimeJson.TextHash(preview.Payload) && !preview.Payload.Contains("test-provider-secret"),
                "Egress manifests must list provenance, hash the exact request, and exclude credentials.");
            await Throws<WorkspaceConflictException>(() => store.DecideEgressAsync("software-studio", preview.ManifestId, new(preview.Version, preview.PayloadSha256, true), false, "approve-missing-key", Ct),
                "Approval cannot queue egress without an available credential reference.");
            await Throws<WorkspaceConflictException>(() => store.DecideEgressAsync("software-studio", preview.ManifestId, new(preview.Version, "wrong-hash", true), true, "approve-wrong-hash", Ct), "A wrong payload hash must never grant egress.");
            using var vault = new RuntimeCredentialVault(); vault.Set("software-studio", "primary", "openai", "test-provider-secret");
            Check(!vault.Status("other-company", "primary", "openai", "memory").Available && !vault.Status("software-studio", "primary", "anthropic", "memory").Available,
                "Memory credentials must be isolated by company and provider type, including when a route ID is reused.");
            var approved = await store.DecideEgressAsync("software-studio", preview.ManifestId, new(preview.Version, preview.PayloadSha256, true), true, "approve", Ct);
            Check(approved.Status == "queued" && approved.ConsentId is not null && approved.Version == 2, "Owner approval must bind the exact request hash and issue a durable consent record.");
            var client = new FixtureCloudClient(); var worker = new CloudEgressWorker(store, vault, client, NullLogger<CloudEgressWorker>.Instance);
            Check(await worker.RunOneAsync(Ct) && client.Calls == 1 && client.Credential == "test-provider-secret" && client.PayloadSha256 == preview.PayloadSha256,
                "The worker may transmit only the approved bytes with the credential resolved at execution time.");
            var completed = (await store.WorkbenchAsync("software-studio", Ct)).EgressManifests.Single(m => m.ManifestId == preview.ManifestId);
            var reply = (await store.WorkbenchAsync("software-studio", Ct)).Interactions.Single(i => i.DedupeKey == "cloud-reply-" + preview.ManifestId);
            Check(completed.Status == "completed" && completed.ResponseSha256 is not null && completed.ResponseMetadataSha256 is not null && reply.Origin == "cloud_model_proposal:primary",
                "Cloud output must become a labelled public proposal with response metadata hashes, never canonical memory.");
            var journal = string.Join('\n', (await store.ReadEventsAsync("software-studio", 0, Ct)).Select(e => JsonSerializer.Serialize(e, RuntimeJson.Options)));
            Check(!journal.Contains("test-provider-secret"), "Raw provider credentials must never enter event storage or backups.");

            var recovery = await store.PrepareEgressAsync("software-studio", message.InteractionId, new("primary", "ba-agent", "ba"), "recovery-preview", Ct);
            recovery = await store.DecideEgressAsync("software-studio", recovery.ManifestId, new(recovery.Version, recovery.PayloadSha256, true), true, "recovery-approve", Ct);
            var staleClaim = (await store.ClaimEgressAsync("old-worker", Ct))!;
            await store.RecoverEgressAsync("replacement-worker", Ct);
            await store.CompleteEgressAsync(staleClaim, new("{\"summary\":\"late result\",\"actionItems\":[],\"citations\":[]}", new(5, 5), "late", "late"), null, Ct);
            Check((await store.WorkbenchAsync("software-studio", Ct)).EgressManifests.Single(m => m.ManifestId == recovery.ManifestId).Status == "interrupted" &&
                !(await store.WorkbenchAsync("software-studio", Ct)).Interactions.Any(i => i.Summary == "late result"), "Restart must fence late cloud output and require an explicit retry.");
            await store.RetryEgressAsync("software-studio", recovery.ManifestId, Ct);
            Check(await worker.RunOneAsync(Ct) && client.Calls == 2, "Explicit cloud retry reuses the approved request with a fresh lease.");

            await store.CreateCompanyAsync(new("private-input", "Private input"), Ct);
            await store.ConfigureCompanyAsync("private-input", new(0, "Private", ["Analysis"], PrivacyMode: "controlled_cloud"), Ct);
            await store.ConfigureCloudProviderAsync("private-input", "primary", new(0, "openai", "gpt-6-astra"), Ct);
            var restricted = await store.ImportSourceAsync("private-input", new("restricted-evidence", "Private source", "Restricted architectural detail.", Classification: "restricted"), Ct);
            var cited = await store.AddInteractionAsync("private-input", new("lobby", "question", "owner", ["ba-agent"], "Discuss this unrelated public summary.", [], [new(restricted.SourceId, 1, 0, restricted.Content.Length, restricted.ContentSha256)]), "cited-question", Ct);
            await Throws<WorkspaceConflictException>(() => store.PrepareEgressAsync("private-input", cited.InteractionId, new("primary", "ba-agent", "ba"), "private-preview", Ct),
                "Restricted provenance in a public interaction must block cloud, even when retrieval does not include the original source excerpt.");

            await store.CreateCompanyAsync(new("classification-test", "Classification test"), Ct);
            await store.ConfigureCompanyAsync("classification-test", new(0, "Classifications", ["Analysis"], PrivacyMode: "controlled_cloud"), Ct);
            await store.ConfigureCloudProviderAsync("classification-test", "primary", new(0, "openai", "gpt-6-astra"), Ct);
            var publicSource = await store.ImportSourceAsync("classification-test", new("public-source", "Public review", "PROVENANCE_MARKER Review public source before delivery.", Classification: "public"), Ct);
            var classificationQuestion = await store.AddInteractionAsync("classification-test", new("lobby", "question", "owner", ["ba-agent"], "Review public source.", [], []), "classification-question", Ct);
            var classifiedPreview = await store.PrepareEgressAsync("classification-test", classificationQuestion.InteractionId, new("primary", "ba-agent", "ba"), "classification-preview", Ct);
            Check(classifiedPreview.Sources.Any(s => s.SourceId == publicSource.SourceId), "Cloud snapshots must retrieve relevant current-revision evidence.");
            var omitted = await store.PrepareEgressAsync("classification-test", classificationQuestion.InteractionId, new("primary", "ba-agent", "ba", ExcludedSourceIds: [publicSource.SourceId]), "excluded-preview", Ct);
            Check(!omitted.Payload.Contains("PROVENANCE_MARKER") && omitted.Redactions.Contains("source-excerpt-omitted:public-source"), "Owner exclusions must remove source excerpts from exact outbound bytes and record the omission.");
            await store.DecideEgressAsync("classification-test", classifiedPreview.ManifestId, new(classifiedPreview.Version, classifiedPreview.PayloadSha256, true), true, "classification-approve", Ct);
            await store.ImportSourceAsync("classification-test", new(publicSource.SourceId, publicSource.Title, publicSource.Content, 1, Classification: "confidential"), Ct);
            Check(await store.ClaimEgressAsync("classification-worker", Ct) is null && (await store.WorkbenchAsync("classification-test", Ct)).EgressManifests.Single(m => m.ManifestId == classifiedPreview.ManifestId).Error == "POLICY_CHANGED_REPREVIEW_REQUIRED",
                "A classification upgrade must invalidate an approved but unsent public-source payload.");

            var storage = await store.VerifyAsync(Ct); await store.MigrateAsync(backend == "file" ? "sqlite" : "file", storage.Digest, Ct);
            var encrypted = await store.ExportBackupAsync("cloud-fixture-backup-passphrase", Ct);
            var bundle = BackupCodec.Decrypt(encrypted, "cloud-fixture-backup-passphrase");
            Check(bundle.Companies["software-studio"].Any(e => e.Type == "cloud.egress.dispatch.authorized") && !Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(bundle)).Contains("test-provider-secret"),
                "Migration and authenticated backup must preserve egress provenance without copying the runtime credential vault.");

            var second = await store.AddInteractionAsync("software-studio", new("lobby", "question", "owner", ["ba-agent"], "Second approved request.", [], []), "cloud-question-2", Ct);
            var pending = await store.PrepareEgressAsync("software-studio", second.InteractionId, new("primary", "ba-agent", "ba"), "cloud-preview-2", Ct);
            pending = await store.DecideEgressAsync("software-studio", pending.ManifestId, new(pending.Version, pending.PayloadSha256, true), true, "approve-2", Ct);
            await store.ConfigureCompanyAsync("software-studio", new(profile.Version, "Back to local", ["Analysis"], PrivacyMode: "strict_local", CloudPolicyVersion: profile.CloudPolicyVersion), Ct);
            Check(await store.ClaimEgressAsync("policy-check", Ct) is null && (await store.WorkbenchAsync("software-studio", Ct)).EgressManifests.Single(m => m.ManifestId == pending.ManifestId).Error == "POLICY_CHANGED_REPREVIEW_REQUIRED",
                "Switching back to Strict Local must fence already queued but not-yet-sent cloud work.");
            vault.Clear("software-studio", "primary", "openai");
            Check(!vault.Status("software-studio", "primary", "openai", "memory").Available, "Owner can clear an ephemeral provider credential without persisting it.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task AdapterContractsAsync()
    {
        var reply = "{\"summary\":\"ok\",\"actionItems\":[],\"citations\":[]}";
        await Verify("openai", "api.openai.com", "/v1/responses", "responses-v1",
            "{\"id\":\"r1\",\"model\":\"gpt-6-astra\",\"status\":\"completed\",\"output\":[{\"content\":[{\"type\":\"output_text\",\"text\":" + JsonSerializer.Serialize(reply) + "}]}],\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}",
            request => Check(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "credential", "OpenAI must use bearer auth without placing the key in the payload."));
        await Verify("anthropic", "api.anthropic.com", "/v1/messages", "2026-10-21",
            "{\"id\":\"m1\",\"model\":\"claude-sonnet-4-6\",\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(reply) + "}],\"usage\":{\"input_tokens\":11,\"output_tokens\":6}}",
            request => Check(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.GetValues("anthropic-version").Single() == "2026-10-21", "Anthropic must pin bearer auth and the current API version header."));
        await Verify("gemini", "generativelanguage.googleapis.com", "/v1beta/interactions", "v1beta",
            "{\"id\":\"g1\",\"model\":\"gemini-3.8-flash\",\"status\":\"completed\",\"steps\":[{\"type\":\"model_output\",\"content\":[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(reply) + "}]}],\"usage\":{\"total_input_tokens\":12,\"total_output_tokens\":7}}",
            request => Check(request.Headers.GetValues("x-goog-api-key").Single() == "credential" && request.Headers.Authorization is null, "Gemini must use x-goog-api-key only on its fixed Interactions endpoint."));
        async Task Verify(string type, string host, string path, string version, string response, Action<HttpRequestMessage> inspect)
        {
            using var adapter = new CloudProviderClient(new StubHandler(request =>
            {
                Check(request.RequestUri?.Scheme == "https" && request.RequestUri.Host == host && request.RequestUri.AbsolutePath == path, "Cloud adapters must reject arbitrary destinations and call the fixed official endpoint."); inspect(request);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
            }));
            var provider = new CloudProviderSnapshot("route", 1, type, "model", host, path, version, "memory", "fixture");
            var configuration = new CloudProviderConfiguration("route", 1, type, "model", host, path, version, "memory", true, "fixture", DateTimeOffset.UtcNow);
            var payload = CloudProviderPayload.Build(configuration, "{\"question\":\"Fixture\"}", 2048);
            var result = await adapter.ExecuteAsync(provider, payload, "credential", Ct);
            Check(result.Text == reply && result.Usage.InputTokens is > 0 && result.Usage.OutputTokens is > 0, "Provider responses must normalize bounded text and measured usage.");
            await Throws<CloudProviderException>(() => adapter.ExecuteAsync(provider with { EndpointHost = "external.invalid" }, payload, "credential", Ct), "Arbitrary hosts must be rejected before HTTP dispatch.");
        }
    }
    private sealed class FixtureCloudClient : ICloudProviderClient
    {
        public int Calls; public string? Credential; public string? PayloadSha256;
        public Task<CloudProviderResult> ExecuteAsync(CloudProviderSnapshot provider, string payload, string credential, CancellationToken ct)
        {
            Calls++; Credential = credential; PayloadSha256 = RuntimeJson.TextHash(payload);
            const string text = "{\"summary\":\"Reviewed cloud proposal\",\"actionItems\":[],\"citations\":[]}";
            return Task.FromResult(new CloudProviderResult(text, new(20, 8), RuntimeJson.TextHash(text), RuntimeJson.Hash(new { provider.ProviderId, status = "completed" })));
        }
    }
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}
