using System.IO.Compression;
using System.Text.Json;
using AICompanyOS.Runtime;

public static class DeliveryTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static int _checks;
    private const string Company = "software-studio";
    public static ArtifactDraft Good => new("Tài liệu dự án", "Static test artifact", [new("index.html", "<html lang=\"vi\"><head><meta charset=\"utf-8\"/><title>Local project</title><link rel=\"stylesheet\" href=\"site.css\"/></head><body><main><h1>Tài liệu dự án</h1><p>Keep data local.</p></main></body></html>"), new("site.css", "body { color: #173b35; margin: 2rem; }"), new("data.json", "{\"local\":true}")]);
    public static async Task RunAsync()
    {
        foreach (var backend in new[] { "file", "sqlite" }) { await FlowAsync(backend); await ModelFlowAsync(backend); }
        Policy();
        Console.WriteLine($"PASS: {_checks} delivery assertions: real artifact bytes, static gate receipts, revision/approval fencing, export, privacy policy, File/SQLite restart/migration/backup parity and local-model Dev context.");
    }
    private static void Check(bool value, string message) { _checks++; if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (Exception e) when (e is ArgumentException or WorkspaceConflictException or ModelProviderException) { _checks++; return; }
        throw new InvalidOperationException(message);
    }
    private static async Task<WorkflowRun> PlanAsync(FileEventStore store, string id)
    {
        var run = (await store.CreateRunAsync(Company, id, "Create a passive local document", [], id, Ct)).Run;
        run = (await store.AcknowledgeHandoffAsync(Company, id, run.Handoff!.ContentSha256, "leader-agent", "ack-" + id, Ct, run.LastSequence)).Run!;
        return (await store.DecideApprovalAsync(Company, id, true, "approve-" + id, Ct, run.LastSequence)).Run!;
    }
    private static async Task FlowAsync(string backend)
    {
        var root = Path.Combine(Path.GetTempPath(), "aicos-delivery-" + Guid.NewGuid().ToString("N"));
        var restoreRoot = root + "-restore";
        try
        {
            string id = "delivery", artifactHash;
            byte[] backup;
            using (var store = new FileEventStore(root, backend))
            {
                await store.InitializeAsync(Ct);
                var run = await PlanAsync(store, id);
                Check(run.Delivery is null && run.State == "approved", "Plan approval cannot implicitly execute a delivery.");
                await store.CreateAgentAsync(Company, new("many-roles", "Delivery team", DeliveryTools.ReviewRoles), Ct);
                var assignments = DeliveryTools.ReviewRoles.ToDictionary(r => r, _ => "many-roles");
                var request = new DeliveryPrepareRequest(run.LastSequence, [new("local", "index.html", "Keep data local.")], "manual", assignments);
                Check((await store.PrepareDeliveryAsync(Company, id, request with { ExpectedSequence = 0 }, "stale", Ct)).Outcome == CommandOutcome.Conflict, "Prepare must reject a stale checkpoint.");
                run = (await store.PrepareDeliveryAsync(Company, id, request, "prepare", Ct)).Run!;
                Check(run.State == "awaiting_delivery_input" && run.Delivery!.Bindings.All(b => b.Agent.AgentId == "many-roles"), "Multiple delivery roles may share one agent without ambiguous bindings.");
                Check(run.Delivery!.Bindings.All(b => b.Agent.ToolGrants.Length == 0) && run.Delivery.WorkflowVersion == DeliveryTools.WorkflowVersion, "Delivery scope does not grant shell/Git and pins its workflow version.");
                Check((await store.PrepareDeliveryAsync(Company, id, request, "prepare", Ct)).Outcome == CommandOutcome.AlreadyApplied, "Prepare replay must not duplicate delivery.");
                Check((await store.PrepareDeliveryAsync(Company, id, request with { Mode = "local_model" }, "prepare", Ct)).Outcome == CommandOutcome.Conflict, "A changed request cannot reuse a delivery key.");
                var dangerous = Good with { Files = [new("index.html", Good.Files[0].Content.Replace("<main>", "<script>alert(1)</script><main>"))] };
                var submitted = new ArtifactSubmitRequest(run.LastSequence, dangerous);
                run = (await store.SubmitArtifactAsync(Company, id, submitted, "unsafe", Ct)).Run!;
                Check(run.State == "delivery_blocked" && run.Delivery!.Receipts!.Single(r => r.RoleId == "soc").ExitCode == 1, "Script-containing output must produce a real blocking SOC receipt.");
                Check(run.Delivery!.Report!.Status == "blocked" && run.Delivery.Report.ReceiptIds.Length == 3, "SubPM must report failures as well as success.");
                Reject(() => DeliveryTools.Export(run), "Critical findings must block export.");
                Reject(() => DeliveryTools.Preview(run.Delivery), "Unsafe preview must be blocked.");
                Check((await store.ReviewDeliveryAsync(Company, id, new(run.LastSequence, run.Delivery.Artifact!.Sha256, true, true, true), "bypass", Ct)).Outcome == CommandOutcome.Conflict, "Owner approval cannot bypass failed static gates.");
                Check((await store.SubmitArtifactAsync(Company, id, submitted, "unsafe", Ct)).Outcome == CommandOutcome.AlreadyApplied, "Submit retry must not consume another artifact version.");
                var correct = new ArtifactSubmitRequest(run.LastSequence, Good);
                run = (await store.SubmitArtifactAsync(Company, id, correct, "correct", Ct)).Run!;
                var session = run.Delivery!;
                artifactHash = session.Artifact!.Sha256;
                Check(run.State == "awaiting_delivery_approval" && session.Artifact.Version == 2 && DeliveryTools.GatesPass(session), "Corrected file bytes must pass all three static gates.");
                Check(session.Receipts!.All(r => r.InputSha256 == artifactHash && r.LogSha256 == RuntimeJson.Hash(r.Checks) && r.StartedAt <= r.FinishedAt), "Receipts bind exact bytes, actual checks, timestamps and log digests.");
                Check(session.Receipts!.All(r => r.SelfReviewed) && session.Report!.ReporterAgentId == "many-roles", "Shared Dev/reviewer identities must be labelled self-reviewed and SubPM must be identified.");
                Check(session.Artifact.Files.All(f => f.Sha256 == RuntimeJson.TextHash(f.Content)), "File digests must hash exact UTF-8 content, not serialized JSON strings.");
                var preview = DeliveryTools.Preview(session);
                Check(preview.Contains("<style>") && !preview.Contains("<link"), "Preview must inline only checked local stylesheets.");
                var report = WorkflowReport.FromRun(run);
                Check(report.EvidenceRefs.Length == 3 && report.OutstandingApprovals.SequenceEqual(["owner-delivery-approval"]) && report.ProducedArtifacts.Contains(session.Artifact.ArtifactId), "Reports must expose actual receipts, artifact and outstanding final approval.");
                Reject(() => DeliveryTools.Export(run), "Passing tools alone must not authorize export.");
                var review = new DeliveryReviewRequest(run.LastSequence, artifactHash, true, true, false);
                Check((await store.ReviewDeliveryAsync(Company, id, review, "missing-rights", Ct)).Outcome == CommandOutcome.Conflict, "Unknown/unconfirmed content rights require human review.");
                Check((await store.ReviewDeliveryAsync(Company, id, review with { RightsConfirmed = true, ArtifactSha256 = "sha256:wrong" }, "wrong-hash", Ct)).Outcome == CommandOutcome.Conflict, "Final approval must match the artifact hash.");
                run = (await store.ControlRunAsync(Company, id, "pause", run.LastSequence, "pause-delivery", Ct)).Run!;
                Check(run.State == "paused" && WorkflowReport.FromRun(run).OutstandingApprovals.Contains("owner-delivery-approval"), "Pause retains the pending delivery approval.");
                Check((await store.ReviewDeliveryAsync(Company, id, review with { ExpectedSequence = run.LastSequence, RightsConfirmed = true }, "paused-review", Ct)).Outcome == CommandOutcome.Conflict, "Paused delivery cannot be approved.");
                run = (await store.ControlRunAsync(Company, id, "resume", run.LastSequence, "resume-delivery", Ct)).Run!;
                Check(run.State == "awaiting_delivery_approval" && run.Delivery!.Artifact!.Sha256 == artifactHash, "Resume must preserve exact artifact and gate.");
                backup = await store.ExportBackupAsync("a-long-local-test-passphrase", Ct);
                var manifest = await store.VerifyAsync(Ct);
                await store.MigrateAsync(backend == "file" ? "sqlite" : "file", manifest.Digest, Ct);
                Check((await store.GetRunAsync(Company, id, Ct))!.Delivery!.Artifact!.Sha256 == artifactHash, "Backend migration must preserve delivery relationships and bytes.");
                await store.CreateCompanyAsync(new("other", "Other company"), Ct);
                Check(await store.GetRunAsync("other", id, Ct) is null, "Delivery lookup cannot cross company boundaries.");
            }
            using (var store = new FileEventStore(root))
            {
                await store.InitializeAsync(Ct);
                var run = (await store.GetRunAsync(Company, id, Ct))!;
                Check(run.State == "awaiting_delivery_approval" && run.Delivery!.Artifact!.Sha256 == artifactHash, "Restart must restore the exact pending gate without re-running checks.");
                var review = new DeliveryReviewRequest(run.LastSequence, artifactHash, true, true, true);
                run = (await store.ReviewDeliveryAsync(Company, id, review, "final", Ct)).Run!;
                Check(run.State == "delivered" && run.Delivery!.Report!.Status == "owner_accepted" && run.Delivery.Report.RemainingReview.Length == 0, "Final Owner decision must update evidenced delivery status.");
                Check((await store.ReviewDeliveryAsync(Company, id, review, "final", Ct)).Outcome == CommandOutcome.AlreadyApplied, "Final review must be idempotent.");
                Check((await store.ReviewDeliveryAsync(Company, id, review with { Approved = false }, "final", Ct)).Outcome == CommandOutcome.Conflict, "Conflicting final review key must be rejected.");
                Check((await store.SubmitArtifactAsync(Company, id, new(run.LastSequence, Good), "late", Ct)).Outcome == CommandOutcome.Conflict, "Accepted artifact bytes cannot be silently replaced.");
                using var zip = new ZipArchive(new MemoryStream(DeliveryTools.Export(run)));
                Check(zip.Entries.Count == Good.Files.Length + 2 && zip.Entries.All(e => !e.FullName.Contains("..")), "Export contains only safe files, checksum list and evidenced report.");
                using var reader = new StreamReader(zip.GetEntry("files/index.html")!.Open());
                Check(await reader.ReadToEndAsync() == Good.Files[0].Content, "Export must preserve exact accepted UTF-8 file contents.");
                var tampered = run.Delivery! with { Receipts = run.Delivery.Receipts!.Select((r, i) => i == 0 ? r with { InputSha256 = "wrong" } : r).ToArray() };
                Reject(() => DeliveryTools.Export(run with { Delivery = tampered }), "Mismatched receipts must block export even for a delivered state.");
                var alteredTool = run.Delivery with { Receipts = run.Delivery.Receipts!.Select((r, i) => i == 0 ? r with { Tool = "model-says-pass" } : r).ToArray() };
                Reject(() => DeliveryTools.Export(run with { Delivery = alteredTool }), "Unrecognized tools cannot satisfy a delivery gate.");
                var versions = await PlanAsync(store, "versions");
                versions = (await store.PrepareDeliveryAsync(Company, versions.RunId, new(versions.LastSequence, [new("missing", "index.html", "unfulfilled-criterion")]), "prepare-versions", Ct)).Run!;
                for (var i = 1; i <= 3; i++)
                {
                    versions = (await store.SubmitArtifactAsync(Company, versions.RunId, new(versions.LastSequence, Good), "version-" + i, Ct)).Run!;
                    Check(versions.State == "delivery_blocked" && versions.Delivery!.Artifact!.Version == i && versions.Delivery.Receipts!.Single(r => r.RoleId == "qa").ExitCode == 1, "Unfulfilled Owner criteria must block every candidate.");
                }
                Check((await store.SubmitArtifactAsync(Company, versions.RunId, new(versions.LastSequence, Good), "fourth", Ct)).Outcome == CommandOutcome.Conflict, "A fourth artifact version cannot bypass the revision budget.");
                versions = (await store.ControlRunAsync(Company, versions.RunId, "cancel", versions.LastSequence, "cancel-versions", Ct)).Run!;
                Check(versions.State == "cancelled", "A blocked delivery can be cancelled at its checkpoint.");
                Reject(() => DeliveryTools.Export(versions), "Cancelled delivery can never export.");
            }
            using (var restored = new FileEventStore(restoreRoot))
            {
                await restored.RestoreIntoEmptyAsync(backup, "a-long-local-test-passphrase", Ct);
                var run = (await restored.GetRunAsync(Company, id, Ct))!;
                Check(run.State == "awaiting_delivery_approval" && run.Delivery!.Artifact!.Sha256 == artifactHash, "Encrypted restore must retain pending approval and artifact provenance.");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); if (Directory.Exists(restoreRoot)) Directory.Delete(restoreRoot, true); }
    }
    private static async Task ModelFlowAsync(string backend)
    {
        var root = Path.Combine(Path.GetTempPath(), "aicos-dev-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new FileEventStore(root, backend); await store.InitializeAsync(Ct);
            var model = new LocalModel("fixture:latest", "sha256:" + new string('a', 64), "test", DateTimeOffset.UtcNow);
            var run = (await store.CreateLocalRunAsync(Company, "ai", "Create a local page", [], model, "ai", Ct)).Run;
            for (var step = 0; step < 2; step++)
            {
                var claim = (await store.ClaimModelJobAsync("w", DateTimeOffset.UtcNow, Ct))!;
                var brief = new RoleBrief(step == 0 ? "BA requirements" : "Leader plan", ["Keep data local."], []);
                await store.FinishModelJobAsync(claim, new(brief, new(10, 10), RuntimeJson.Hash(brief)), null, Ct);
                run = (await store.GetRunAsync(Company, run.RunId, Ct))!;
                if (step == 0) run = (await store.AcknowledgeHandoffAsync(Company, run.RunId, run.Handoff!.ContentSha256, "leader-agent", "ack", Ct, run.LastSequence)).Run!;
            }
            run = (await store.DecideApprovalAsync(Company, run.RunId, true, "owner", Ct, run.LastSequence)).Run!;
            run = (await store.PrepareDeliveryAsync(Company, run.RunId, new(run.LastSequence, [new("privacy", "index.html", "Keep data local.")], "local_model"), "prepare", Ct)).Run!;
            var job = (await store.ClaimModelJobAsync("w", DateTimeOffset.UtcNow, Ct))!;
            Check(run.State == "queued" && job.RoleId == "dev" && job.Model.Digest == model.Digest && job.Context.Delivery!.Criteria.Single().Id == "privacy", "Explicit AI delivery pins Dev role, original model and Owner checks.");
            Check(job.Context.PriorOutputs.Single().RoleId == "leader" && job.Context.Delivery!.PlanSteps.Contains("Keep data local.") && job.Context.Permissions.Length == 0, "Dev receives the actual Leader result/plan without execution permission.");
            using var payload = JsonDocument.Parse(JsonSerializer.Serialize(OllamaProvider.Payload(job), RuntimeJson.Options));
            Check(payload.RootElement.GetProperty("format").GetProperty("required").EnumerateArray().Any(x => x.GetString() == "files") && OllamaProvider.TokenLimit(job) == 4096, "Dev uses an artifact schema and bounded output budget.");
            await store.RecoverModelJobsAsync("replacement-worker", Ct);
            run = (await store.GetRunAsync(Company, run.RunId, Ct))!;
            Check(run.State == "blocked" && run.Delivery!.Criteria.Single().Id == "privacy" && run.Delivery.Artifact is null, "An orphan Dev lease must preserve the approved criteria without adopting output.");
            Check(!await store.FinishModelJobAsync(job, null, "late", Ct), "Orphan Dev lease cannot finish after restart recovery.");
            run = (await store.RetryModelJobAsync(Company, run.RunId, run.LastSequence, "retry-dev", Ct)).Run!;
            var retry = (await store.ClaimModelJobAsync("replacement-worker", DateTimeOffset.UtcNow, Ct))!;
            Check(retry.InputSha256 == job.InputSha256 && retry.Attempts == 2, "Dev restart retry must preserve exact pinned input and consume the remaining attempt.");
            job = retry;
            var draft = DeliveryTools.ParseDraft(JsonSerializer.Serialize(Good, RuntimeJson.Options));
            Check(await store.FinishModelJobAsync(job, new(new(draft.Summary, draft.Files.Select(f => f.Path).ToArray(), []), new(20, 20), RuntimeJson.Hash(draft), draft), null, Ct), "A fenced Dev completion must commit artifact and checks atomically.");
            run = (await store.GetRunAsync(Company, run.RunId, Ct))!;
            Check(run.State == "awaiting_delivery_approval" && run.Delivery!.Artifact!.Origin == "local_model" && run.Delivery.Artifact.JobId == job.JobId && DeliveryTools.GatesPass(run.Delivery), "Actual Dev artifact must link model provenance and wait for final Owner approval.");
            Check(!await store.FinishModelJobAsync(job, null, "late", Ct), "Late Dev completion cannot overwrite artifact or receipts.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void Policy()
    {
        var bindings = DeliveryTools.ReviewRoles.Select(r => new DeliveryBinding(r, new(r, r, [r], 1, []))).ToArray();
        var session = new DeliverySession(DeliveryTools.WorkflowVersion, "manual", bindings, [new("heading", "index.html", "<h1>")]);
        bool Pass(ArtifactDraft draft) { var a = DeliveryTools.Produce(draft, 1, "owner_supplied", null); return DeliveryTools.GatesPass(session with { Artifact = a, Receipts = DeliveryTools.Check(a, session) }); }
        foreach (var path in new[] { "../index.html", "/index.html", "x\\index.html", "C:site.html", "index.js", "INDEX.HTML", "a/../index.html" })
            Reject(() => DeliveryTools.ValidateDraft(Good with { Files = [new(path, "x")] }), "Unsafe export path must be rejected: " + path);
        foreach (var insertion in new[] { "<script>alert(1)</script>", "<img src=\"https://outside.invalid/pixel\"/>", "<iframe src=\"http://127.0.0.1:4173/api/session\"></iframe>", "<form action=\"https://outside.invalid\"></form>", "<meta http-equiv=\"refresh\" content=\"0;url=https://outside.invalid\"/>", "<a href=\"javascript:alert(1)\">x</a>", "<p onclick=\"alert(1)\">x</p>", "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>", "<!-- </style><script>alert(1)</script> -->", "<?xml-stylesheet href=\"https://outside.invalid\"?>", "<style/>" })
            Check(!Pass(Good with { Files = [new("index.html", Good.Files[0].Content.Replace("<main>", insertion + "<main>")), Good.Files[1]] }), "Active/mutating/remote markup must block the SOC gate.");
        foreach (var css in new[] { "body{background:url(https://outside.invalid)}", "@import 'https://outside.invalid';", "body{background:u/**/rl(https://outside.invalid)}", "body{background:u\\72l(https://outside.invalid)}", "body{background:image('https://outside.invalid')} ", "body{background:src('https://outside.invalid')} ", "body{behavior:expression(x)}", "/*unterminated", "</style><script>x</script>" })
            Check(!Pass(Good with { Files = [Good.Files[0], new("site.css", css)] }), "CSS must not activate networking or bypass static policy.");
        Check(!Pass(Good with { Files = [new("index.html", "<!DOCTYPE html [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><html><head><title>x</title></head><body><h1>&x;</h1></body></html>")] }), "DTD/external entity resolution must be prohibited.");
        Reject(() => DeliveryTools.ValidateDraft(Good with { Files = [Good.Files[0], Good.Files[0]] }), "Duplicate file names cannot overwrite entries in ZIP.");
        Reject(() => DeliveryTools.ParseDraft("{\"title\":\"x\",\"summary\":\"x\",\"files\":[],\"approved\":true}"), "Model-supplied approval fields must be rejected.");
        Reject(() => DeliveryTools.ValidateCriteria([new("x", "index.html", "x"), new("x", "index.html", "y")]), "Duplicate acceptance IDs must be rejected.");
        Check(Pass(Good), "Valid local static documents and CSS must pass the bounded policy.");
    }
}
