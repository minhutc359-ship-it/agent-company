using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AICompanyOS.Runtime;

public sealed record SourceRevision(string SourceId, int Revision, string Title, string Content, string ContentSha256, DateTimeOffset ImportedAt);
public sealed record ImportSourceRequest(string SourceId, string Title, string Content, int ExpectedRevision = 0);
public sealed record Citation(string SourceId, int Revision, int Start, int End, string ContentSha256);
public sealed record SearchHit(string Title, string Excerpt, Citation Citation, int MatchedTerms);
public sealed record SearchResult(string Mode, SearchHit[] Hits, string[] Warnings);
public sealed record MemoryProposalRequest(string ProposalId, string StableKey, string Kind, string Body, Citation[] Citations);
public sealed record MemoryProposal(string ProposalId, string StableKey, string Kind, string Body, Citation[] Citations, string Status);
public sealed record MemoryReviewRequest(bool Accepted, int ExpectedCanonicalVersion);
public sealed record MemoryRecord(string StableKey, string Kind, string Body, Citation[] Citations, int Version, string ApprovedBy, string ProposalId);

public static partial class SourceSafety
{
    [GeneratedRegex(@"-----BEGIN (?:[A-Z ]+ )?PRIVATE KEY-----|\b(?:sk-(?:proj-|ant-)?[A-Za-z0-9_-]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16})\b|(?i:\b(?:api[_-]?key|password|client[_-]?secret|authorization)\s*[:=]\s*[""']?(?:Bearer\s+)?[A-Za-z0-9_+/=.-]{12,})", RegexOptions.CultureInvariant, 100)]
    private static partial Regex SecretPattern();
    public static bool ContainsKnownSecret(string content) => SecretPattern().IsMatch(content);
}

public sealed partial class FileEventStore
{
    private static SourceRevision[] SourcesFrom(IEnumerable<DomainEvent> events) => events.Where(e => e.Type == "source.imported")
        .Select(e => e.Data.Deserialize<SourceRevision>(JsonOptions)!).ToArray();
    private static MemoryRecord[] CanonicalFrom(IEnumerable<DomainEvent> events) => events.Where(e => e.Type == "memory.accepted")
        .Select(e => e.Data.Deserialize<MemoryRecord>(JsonOptions)!).GroupBy(r => r.StableKey).Select(g => g.Last()).OrderBy(r => r.StableKey, StringComparer.Ordinal).ToArray();
    private static MemoryProposal[] ProposalsFrom(IEnumerable<DomainEvent> events)
    {
        var items = new Dictionary<string, MemoryProposal>();
        foreach (var e in events)
        {
            if (e.Type == "memory.proposed")
            {
                var p = e.Data.Deserialize<MemoryProposal>(JsonOptions)!;
                items[p.ProposalId] = p;
            }
            if (e.Type is "memory.accepted" or "memory.rejected")
            {
                var id = ReadString(e.Data, "proposalId")!;
                if (items.TryGetValue(id, out var p)) items[id] = p with { Status = e.Type == "memory.accepted" ? "accepted" : "rejected" };
            }
        }
        return items.Values.ToArray();
    }
    private static void ValidateCitations(Citation[]? citations, SourceRevision[] sources)
    {
        if (citations is not { Length: > 0 and <= 20 }) throw new ArgumentException("At least one source citation is required.");
        foreach (var c in citations)
        {
            if (c is null) throw new ArgumentException("Citation entries must not be null.");
            var source = sources.FirstOrDefault(s => s.SourceId == c.SourceId && s.Revision == c.Revision);
            if (source is null || source.ContentSha256 != c.ContentSha256 || c.Start < 0 || c.End <= c.Start || c.End > source.Content.Length)
                throw new ArgumentException("A citation does not resolve to this company's exact source revision and UTF-16 range.");
        }
    }
    public async Task<SourceRevision> ImportSourceAsync(string companyId, ImportSourceRequest request, CancellationToken ct)
    {
        if (!Identifiers.IsValid(request.SourceId) || string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 160 ||
            string.IsNullOrWhiteSpace(request.Content) || request.Content.Length > 65536 || request.ExpectedRevision < 0)
            throw new ArgumentException("A source needs an ID, title and at most 65,536 UTF-16 characters.");
        if (request.Title.StartsWith(".env", StringComparison.OrdinalIgnoreCase) || SourceSafety.ContainsKnownSecret(request.Title) || SourceSafety.ContainsKnownSecret(request.Content))
            throw new ArgumentException("Possible credentials detected. Remove secrets before importing.");
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(companyId, 0, ct);
            var current = SourcesFrom(events).LastOrDefault(s => s.SourceId == request.SourceId);
            var hash = RuntimeJson.TextHash(request.Content);
            if (current is not null && current.ContentSha256 == hash && current.Title == request.Title.Trim()) return current;
            if ((current?.Revision ?? 0) != request.ExpectedRevision) throw new WorkspaceConflictException("Source revision changed; reload before importing.");
            var source = new SourceRevision(request.SourceId, request.ExpectedRevision + 1, request.Title.Trim(), request.Content, hash, DateTimeOffset.UtcNow);
            await AppendUnsafeAsync(companyId, request.SourceId, [DomainEvent.Create("source.imported", source)], events, ct);
            return source;
        }
        finally { _gate.Release(); }
    }

    public async Task<SourceRevision[]> ListSourcesAsync(string companyId, CancellationToken ct) => SourcesFrom(await ReadEventsAsync(companyId, 0, ct))
        .GroupBy(s => s.SourceId).Select(g => g.Last()).ToArray();

    public async Task<SourceRevision?> GetSourceAsync(string companyId, string sourceId, int revision, CancellationToken ct) =>
        SourcesFrom(await ReadEventsAsync(companyId, 0, ct)).FirstOrDefault(s => s.SourceId == sourceId && s.Revision == revision);

    public async Task<SearchResult> SearchAsync(string companyId, string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 300) throw new ArgumentException("Enter a search query up to 300 characters.");
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();
        var compare = CultureInfo.InvariantCulture.CompareInfo;
        const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        var hits = new List<SearchHit>();
        foreach (var source in await ListSourcesAsync(companyId, ct))
        {
            var offsets = terms.Select(term => compare.IndexOf(source.Content, term, options)).Where(i => i >= 0).ToArray();
            if (offsets.Length == 0) continue;
            var start = Math.Max(0, offsets.Min() - 80);
            if (start > 0 && char.IsLowSurrogate(source.Content[start])) start--;
            var end = Math.Min(source.Content.Length, start + 400);
            if (end < source.Content.Length && char.IsLowSurrogate(source.Content[end])) end--;
            hits.Add(new(source.Title, source.Content[start..end], new(source.SourceId, source.Revision, start, end, source.ContentSha256), offsets.Length));
        }
        return new("lexical-only", hits.OrderByDescending(h => h.MatchedTerms).ThenBy(h => h.Title).Take(10).ToArray(),
            ["No semantic index is configured. Search results are untrusted source text, not accepted facts."]);
    }

    public async Task<MemoryProposal[]> ListProposalsAsync(string companyId, CancellationToken ct) => ProposalsFrom(await ReadEventsAsync(companyId, 0, ct));
    public async Task<MemoryRecord[]> ListCanonicalAsync(string companyId, CancellationToken ct) => CanonicalFrom(await ReadEventsAsync(companyId, 0, ct));

    public async Task<MemoryProposal> ProposeMemoryAsync(string companyId, MemoryProposalRequest request, CancellationToken ct)
    {
        if (!Identifiers.IsValid(request.ProposalId) || !Identifiers.IsValid(request.StableKey) || request.Kind is not ("fact" or "decision" or "constraint") ||
            string.IsNullOrWhiteSpace(request.Body) || request.Body.Length > 4000 || SourceSafety.ContainsKnownSecret(request.Body))
            throw new ArgumentException("Invalid memory proposal, kind or content.");
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(companyId, 0, ct);
            ValidateCitations(request.Citations, SourcesFrom(events));
            var proposal = new MemoryProposal(request.ProposalId, request.StableKey, request.Kind, request.Body.Trim(), request.Citations, "proposed");
            var prior = ProposalsFrom(events).FirstOrDefault(p => p.ProposalId == proposal.ProposalId);
            if (prior is not null) return RuntimeJson.Hash(prior with { Status = "proposed" }) == RuntimeJson.Hash(proposal)
                ? prior : throw new WorkspaceConflictException("Proposal ID belongs to another payload.");
            await AppendUnsafeAsync(companyId, proposal.ProposalId, [DomainEvent.Create("memory.proposed", proposal)], events, ct);
            return proposal;
        }
        finally { _gate.Release(); }
    }

    public async Task<MemoryProposal> ReviewMemoryAsync(string companyId, string proposalId, MemoryReviewRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var events = await ReadEventsUnsafeAsync(companyId, 0, ct);
            var proposal = ProposalsFrom(events).FirstOrDefault(p => p.ProposalId == proposalId) ?? throw new KeyNotFoundException("Proposal not found.");
            var status = request.Accepted ? "accepted" : "rejected";
            if (proposal.Status == status) return proposal;
            if (proposal.Status != "proposed") throw new WorkspaceConflictException("Proposal was already reviewed.");
            ValidateCitations(proposal.Citations, SourcesFrom(events));
            var canonical = CanonicalFrom(events).FirstOrDefault(r => r.StableKey == proposal.StableKey);
            if ((canonical?.Version ?? 0) != request.ExpectedCanonicalVersion) throw new WorkspaceConflictException("Canonical record changed; review the latest version.");
            if (request.Accepted)
            {
                var sources = SourcesFrom(events);
                if (proposal.Citations.Any(c => sources.Where(s => s.SourceId == c.SourceId).Max(s => s.Revision) != c.Revision))
                    throw new WorkspaceConflictException("A cited source has a newer revision; review and propose memory from current evidence.");
                var record = new MemoryRecord(proposal.StableKey, proposal.Kind, proposal.Body, proposal.Citations, request.ExpectedCanonicalVersion + 1, "local-owner", proposalId);
                await AppendUnsafeAsync(companyId, proposalId, [DomainEvent.Create("memory.accepted", record)], events, ct);
            }
            else await AppendUnsafeAsync(companyId, proposalId, [DomainEvent.Create("memory.rejected", new { proposalId, approvedBy = "local-owner" })], events, ct);
            return proposal with { Status = status };
        }
        finally { _gate.Release(); }
    }
}
