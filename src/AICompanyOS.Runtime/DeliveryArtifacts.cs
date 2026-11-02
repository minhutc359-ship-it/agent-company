using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AICompanyOS.Runtime;

public sealed record DraftFile(string Path, string Content);
public sealed record ArtifactDraft(string Title, string Summary, DraftFile[] Files);
public sealed record AcceptanceCriterion(string Id, string Path, string Contains);
public sealed record DeliveryWork(string WorkflowVersion, string[] PlanSteps, AcceptanceCriterion[] Criteria);
public sealed record DeliveryPrepareRequest(long ExpectedSequence, AcceptanceCriterion[] Criteria, string Mode = "manual", Dictionary<string, string>? Assignments = null);
public sealed record ArtifactSubmitRequest(long ExpectedSequence, ArtifactDraft Draft);
public sealed record DeliveryReviewRequest(long ExpectedSequence, string ArtifactSha256, bool Approved, bool AcceptanceConfirmed, bool RightsConfirmed);
public sealed record ArtifactFile(string Path, string Content, string Sha256);
public sealed record DeliveryArtifact(string ArtifactId, int Version, string Origin, string? JobId, ArtifactDraft Draft,
    ArtifactFile[] Files, string Sha256, DateTimeOffset ProducedAt);
public sealed record CheckResult(string Check, bool Passed, string Message);
public sealed record CheckReceipt(string ReceiptId, string RoleId, string AgentId, string Tool, string ToolVersion,
    string InputSha256, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, int ExitCode, CheckResult[] Checks, string LogSha256, bool SelfReviewed);
public sealed record DeliverySummary(string Status, string ArtifactSha256, string[] ReceiptIds, string[] RemainingReview, string[] Limitations, string ReporterAgentId, DateTimeOffset ProducedAt);
public sealed record DeliveryBinding(string RoleId, Agent Agent);
public sealed record DeliverySession(string WorkflowVersion, string Mode, DeliveryBinding[] Bindings, AcceptanceCriterion[] Criteria,
    DeliveryArtifact? Artifact = null, CheckReceipt[]? Receipts = null, DeliverySummary? Report = null,
    bool? OwnerAccepted = null, bool AcceptanceConfirmed = false, bool RightsConfirmed = false);

/// <summary>Bounded, in-process static-document tools. Never executes generated code or resolves a filesystem path.</summary>
public static partial class DeliveryTools
{
    public const string WorkflowVersion = "static-delivery/1.0.0";
    public const int MaximumVersions = 3;
    public static readonly string[] ReviewRoles = ["dev", "tester", "soc", "qa", "subpm"];
    public const string PreviewPolicy = "default-src 'none'; style-src 'unsafe-inline'; script-src 'none'; connect-src 'none'; img-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'self'; sandbox";
    private static readonly HashSet<string> Tags = new("html head title meta link style body header nav main section article aside footer h1 h2 h3 h4 h5 h6 p div span ul ol li dl dt dd strong em b i u s small blockquote pre code br hr a table thead tbody tfoot tr th td caption figure figcaption".Split(' '));
    private static readonly HashSet<string> Attributes = new("id class lang dir title charset name content href rel scope colspan rowspan role aria-label aria-labelledby aria-describedby".Split(' '));
    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,47}\.(?:html|css|txt|md|json)$", RegexOptions.CultureInvariant)]
    private static partial Regex SafePathPattern();
    public static bool SafePath(string? path) => path is not null && SafePathPattern().IsMatch(path);
    public static void ValidateCriteria(AcceptanceCriterion[]? criteria)
    {
        if (criteria is not { Length: > 0 and <= 12 } || criteria.Any(c => c is null || !Identifiers.IsValid(c.Id) || !SafePath(c.Path) ||
            string.IsNullOrWhiteSpace(c.Contains) || c.Contains.Length > 500 || SourceSafety.ContainsKnownSecret(c.Contains)) || criteria.Select(c => c.Id).Distinct().Count() != criteria.Length)
            throw new ArgumentException("Provide 1–12 uniquely named literal acceptance checks with safe root-level paths.");
    }
    public static ArtifactDraft ValidateDraft(ArtifactDraft? draft)
    {
        if (draft is null || string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > 160 || string.IsNullOrWhiteSpace(draft.Summary) || draft.Summary.Length > 2000 ||
            draft.Files is not { Length: > 0 and <= 8 } || draft.Files.Any(f => f is null || !SafePath(f.Path) || f.Content is null || f.Content.Length > 32768 || f.Content.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))) ||
            draft.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != draft.Files.Length ||
            draft.Files.Sum(f => Encoding.UTF8.GetByteCount(f.Content)) > 48 * 1024 || !draft.Files.Any(f => f.Path == "index.html"))
            throw new ArgumentException("A static deliverable needs a title, summary and index.html; at most 8 safe text files and 48 KiB UTF-8 are supported.");
        if (SourceSafety.ContainsKnownSecret(draft.Title + "\n" + draft.Summary + "\n" + string.Join('\n', draft.Files.Select(f => f.Content))))
            throw new ArgumentException("Possible credentials must be removed before storing a deliverable.");
        return draft;
    }
    public static ArtifactDraft ParseDraft(string content)
    {
        try
        {
            using var json = JsonDocument.Parse(content);
            ExactKeys(json.RootElement, "title", "summary", "files");
            var files = json.RootElement.GetProperty("files");
            if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() > 8) throw new ArgumentException("Invalid file list.");
            foreach (var f in files.EnumerateArray()) ExactKeys(f, "path", "content");
            return ValidateDraft(json.RootElement.Deserialize<ArtifactDraft>(RuntimeJson.Options));
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        { throw new ModelProviderException("MODEL_ARTIFACT_SCHEMA_INVALID"); }
    }
    private static void ExactKeys(JsonElement value, params string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected object.");
        var actual = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (actual.Length != keys.Length || actual.Distinct().Count() != keys.Length || actual.Except(keys).Any()) throw new ArgumentException("Unexpected or duplicate field.");
    }
    public static DeliveryArtifact Produce(ArtifactDraft draft, int version, string origin, string? jobId)
    {
        ValidateDraft(draft);
        var files = draft.Files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => new ArtifactFile(f.Path, f.Content, RuntimeJson.TextHash(f.Content))).ToArray();
        return new("artifact-" + Guid.NewGuid().ToString("N"), version, origin, jobId, draft, files,
            RuntimeJson.Hash(new { schema = WorkflowVersion, draft, files }), DateTimeOffset.UtcNow);
    }
    public static bool Intact(DeliveryArtifact artifact) => artifact.Files.Select(f => new DraftFile(f.Path, f.Content)).OrderBy(f => f.Path)
        .SequenceEqual(artifact.Draft.Files.OrderBy(f => f.Path)) && artifact.Files.All(f => f.Sha256 == RuntimeJson.TextHash(f.Content)) &&
        artifact.Sha256 == RuntimeJson.Hash(new { schema = WorkflowVersion, draft = artifact.Draft, files = artifact.Files });

    private static XDocument ReadHtml(string content)
    {
        using var reader = XmlReader.Create(new StringReader(content), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32768, MaxCharactersFromEntities = 1024 });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }
    private static bool SafeCss(string css)
    {
        var tokens = new StringBuilder();
        for (var i = 0; i < css.Length; i++)
        {
            if (char.IsWhiteSpace(css[i])) continue;
            if (css[i] == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) return false;
                i = end + 1; continue;
            }
            tokens.Append(char.ToLowerInvariant(css[i]));
        }
        var compact = tokens.ToString();
        return !compact.Any(c => c is '\\' or '@' or '<' or '>') && !new[] { "url(", "src(", "image(", "image-set(", "cross-fade(", "paint(", "expression(", "behavior:", "-moz-binding" }.Any(compact.Contains);
    }
    private static bool LocalLink(string value, DeliveryArtifact artifact, bool cssOnly = false)
    {
        var split = value.Split('#', 2);
        if (split.Length == 2 && (split[1].Length == 0 || !split[1].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return false;
        return split[0].Length == 0 ? !cssOnly && split.Length == 2 : SafePath(split[0]) && (!cssOnly || split.Length == 1 && split[0].EndsWith(".css", StringComparison.Ordinal)) && artifact.Files.Any(f => f.Path == split[0]);
    }
    private static bool PassiveHtml(XDocument doc, DeliveryArtifact artifact)
    {
        if (doc.Nodes().Any(n => n is XProcessingInstruction or XComment) || doc.DescendantNodes().Any(n => n is XProcessingInstruction or XComment or XCData)) return false;
        if (SourceSafety.ContainsKnownSecret(string.Join('\n', doc.Descendants().SelectMany(e => e.Attributes().Select(a => a.Value))) + "\n" + doc.Root?.Value)) return false;
        foreach (var element in doc.Descendants())
        {
            var tag = element.Name.LocalName;
            if (element.Name.NamespaceName is not ("" or "http://www.w3.org/1999/xhtml") || !Tags.Contains(tag) || element.IsEmpty && tag is not ("meta" or "link" or "br" or "hr")) return false;
            foreach (var a in element.Attributes())
            {
                if (a.IsNamespaceDeclaration) { if (a.Name.LocalName != "xmlns" || a.Value != "http://www.w3.org/1999/xhtml") return false; continue; }
                if (a.Name.NamespaceName.Length != 0 || !Attributes.Contains(a.Name.LocalName)) return false;
                if (a.Name.LocalName == "href" && (tag is not ("a" or "link") || !LocalLink(a.Value, artifact, tag == "link"))) return false;
            }
            if (tag == "style" && (element.HasElements || !SafeCss(element.Value))) return false;
            if (tag == "link" && ((string?)element.Attribute("rel") != "stylesheet" || element.Attribute("href") is null)) return false;
            if (tag == "meta" && !((element.Attributes().Count() == 1 && (string?)element.Attribute("charset") == "utf-8") ||
                (element.Attributes().Count() == 2 && (string?)element.Attribute("name") == "viewport" && (string?)element.Attribute("content") == "width=device-width, initial-scale=1"))) return false;
        }
        return true;
    }
    public static CheckReceipt[] Check(DeliveryArtifact artifact, DeliverySession session)
    {
        var started = DateTimeOffset.UtcNow;
        var tests = new List<CheckResult> { new("manifest.sha256", Intact(artifact), "Exact file bytes and manifest digest.") };
        var security = new List<CheckResult>();
        foreach (var file in artifact.Files)
        {
            if (file.Path.EndsWith(".html", StringComparison.Ordinal))
            {
                try
                {
                    var doc = ReadHtml(file.Content);
                    tests.Add(new(file.Path + ":structure", doc.Root?.Name.LocalName == "html" && doc.Root.Elements().Count(e => e.Name.LocalName == "head") == 1 &&
                        doc.Root.Elements().Count(e => e.Name.LocalName == "body") == 1 && doc.Descendants().Any(e => e.Name.LocalName == "title" && !string.IsNullOrWhiteSpace(e.Value)) &&
                        doc.Descendants().Any(e => e.Name.LocalName == "h1" && !string.IsNullOrWhiteSpace(e.Value)), "XHTML document, head/body, title and primary heading."));
                    security.Add(new(file.Path + ":passive", PassiveHtml(doc, artifact), "Allowlisted passive markup; no scripts, forms, embeds, external URLs, DTDs or active attributes."));
                }
                catch (XmlException) { tests.Add(new(file.Path + ":parse", false, "Valid bounded XHTML without DTD/entities is required.")); security.Add(new(file.Path + ":parse", false, "Unparsed markup cannot pass security policy.")); }
            }
            else if (file.Path.EndsWith(".css", StringComparison.Ordinal))
                security.Add(new(file.Path + ":css", SafeCss(file.Content), "CSS must not contain URL functions, at-rules, escapes or active constructs."));
            else if (file.Path.EndsWith(".json", StringComparison.Ordinal))
            {
                try { using var json = JsonDocument.Parse(file.Content); tests.Add(new(file.Path + ":json", true, "JSON parsed successfully.")); }
                catch (JsonException) { tests.Add(new(file.Path + ":json", false, "Invalid JSON.")); }
            }
        }
        security.Add(new("credential.patterns", !artifact.Files.Any(f => SourceSafety.ContainsKnownSecret(f.Content)), "Known credential patterns are blocked; this is not a comprehensive secret scanner."));
        var quality = session.Criteria.Select(c => new CheckResult(c.Id,
            artifact.Files.Any(f => f.Path == c.Path && f.Content.Contains(c.Contains, StringComparison.Ordinal)), $"Owner literal criterion in {c.Path}.")).ToArray();
        return [Receipt("tester", tests.ToArray()), Receipt("soc", security.ToArray()), Receipt("qa", quality)];
        CheckReceipt Receipt(string role, CheckResult[] checks)
        {
            return new("receipt-" + Guid.NewGuid().ToString("N"), role, session.Bindings.Single(b => b.RoleId == role).Agent.AgentId,
                "static-document." + role, "1.0.0", artifact.Sha256, started, DateTimeOffset.UtcNow, checks.All(c => c.Passed) ? 0 : 1, checks, RuntimeJson.Hash(checks),
                session.Bindings.Single(b => b.RoleId == role).Agent.AgentId == session.Bindings.Single(b => b.RoleId == "dev").Agent.AgentId);
        }
    }
    public static bool GatesPass(DeliverySession session) => session.Artifact is not null && Intact(session.Artifact) &&
        session.WorkflowVersion == WorkflowVersion && session.Receipts is { Length: 3 } receipts && new[] { "tester", "soc", "qa" }.All(role => receipts.Count(r => r.RoleId == role && r.ExitCode == 0 &&
            r.Tool == "static-document." + role && r.ToolVersion == "1.0.0" && r.AgentId == session.Bindings.Single(b => b.RoleId == role).Agent.AgentId && r.StartedAt <= r.FinishedAt &&
            r.Checks.Length > 0 && r.Checks.All(c => c.Passed) && r.InputSha256 == session.Artifact.Sha256 && r.LogSha256 == RuntimeJson.Hash(r.Checks)) == 1);
    public static DeliverySummary Summarize(DeliverySession session) => new(GatesPass(session) ? "ready_for_owner" : "blocked", session.Artifact!.Sha256,
        session.Receipts!.Select(r => r.ReceiptId).ToArray(), ["Owner must check meaning, acceptance and rights/provenance before export."],
        ["Checks cover passive static documents only, not application builds, JavaScript execution, dependency CVEs or legal clearance.", "No shell, repository writes, publish or Git permissions were granted."],
        session.Bindings.Single(b => b.RoleId == "subpm").Agent.AgentId, DateTimeOffset.UtcNow);
    public static string Preview(DeliverySession session)
    {
        if (!GatesPass(session)) throw new WorkspaceConflictException("Preview requires all static-document gates to pass.");
        var doc = ReadHtml(session.Artifact!.Files.Single(f => f.Path == "index.html").Content);
        // Inline only already checked CSS. The response and iframe each enforce sandboxing without scripts or network access.
        foreach (var link in doc.Descendants().Where(e => e.Name.LocalName == "link").ToArray())
        {
            var css = session.Artifact.Files.Single(f => f.Path == (string?)link.Attribute("href")).Content;
            link.ReplaceWith(new XElement(link.Name.Namespace + "style", css));
        }
        return doc.ToString(SaveOptions.DisableFormatting);
    }
    public static byte[] Export(WorkflowRun run)
    {
        var session = run.Delivery;
        if (run.State != "delivered" || session?.OwnerAccepted != true || !session.AcceptanceConfirmed || !session.RightsConfirmed || !GatesPass(session))
            throw new WorkspaceConflictException("Export requires passing receipts and Owner approval of this exact artifact.");
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            foreach (var f in session.Artifact!.Files) Write("files/" + f.Path, f.Content);
            Write("delivery-report.json", JsonSerializer.Serialize(WorkflowReport.FromRun(run), RuntimeJson.Options));
            Write("SHA256SUMS.txt", string.Join('\n', session.Artifact.Files.Select(f => f.Sha256[7..] + "  files/" + f.Path)) + "\n");
            void Write(string path, string text) { using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); writer.Write(text); }
        }
        return bytes.ToArray();
    }
}
