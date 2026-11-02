using System.Globalization;
using System.Text;

namespace AICompanyOS.Runtime;

public sealed record KnowledgeIndexStatus(string Mode, string IndexVersion, int Sources, int Chunks, string Digest, string[] Capabilities, string[] Limitations);

internal sealed record KnowledgeChunk(SourceRevision Source, int Start, int End, string Text, string[] Terms, double[] Vector);

internal static class KnowledgeIndex
{
    private const int Dimensions = 256;
    private const string Version = "local-hybrid-v1";

    public static KnowledgeIndexStatus Status(SourceRevision[] sources)
    {
        var chunks = Build(sources);
        return new("hybrid-local-vector", Version, sources.Length, chunks.Length,
            RuntimeJson.Hash(chunks.Select(c => new { c.Source.SourceId, c.Source.Revision, c.Start, c.End, c.Source.ContentSha256 }).ToArray()),
            ["unicode lexical terms", "character n-gram vector similarity", "exact source revision/range citations", "canonical-record boost"],
            ["The vector is deterministic local feature hashing, not a neural embedding or ANN index.", "Search text remains untrusted evidence until Owner review."]);
    }

    public static SearchResult Search(SourceRevision[] sources, MemoryRecord[] canonical, string query)
    {
        var queryTerms = Terms(query);
        if (queryTerms.Length == 0) throw new ArgumentException("Enter a query containing letters or numbers.");
        var queryVector = Vector(query);
        var currentCanonical = canonical.SelectMany(record => record.Citations.Select(c => (record, citation: c))).ToArray();
        var hits = new List<SearchHit>();
        foreach (var chunk in Build(sources))
        {
            var matched = queryTerms.Count(term => chunk.Terms.Contains(term, StringComparer.Ordinal));
            var lexical = (double)matched / queryTerms.Length;
            var vector = Cosine(queryVector, chunk.Vector);
            if (matched == 0 && vector < 0.12) continue;
            var accepted = currentCanonical.Any(item => item.citation.SourceId == chunk.Source.SourceId &&
                item.citation.Revision == chunk.Source.Revision && item.citation.ContentSha256 == chunk.Source.ContentSha256 &&
                item.citation.Start < chunk.End && item.citation.End > chunk.Start);
            var score = Math.Min(1, lexical * 0.68 + vector * 0.27 + (accepted ? 0.05 : 0));
            hits.Add(new(chunk.Source.Title, chunk.Text, new(chunk.Source.SourceId, chunk.Source.Revision, chunk.Start, chunk.End, chunk.Source.ContentSha256),
                matched, Math.Round(score, 6), Math.Round(lexical, 6), Math.Round(vector, 6), accepted ? "accepted-support" : "source-only", chunk.Source.Classification));
        }
        var ordered = hits.OrderByDescending(h => h.Score).ThenByDescending(h => h.MatchedTerms).ThenBy(h => h.Title, StringComparer.Ordinal).Take(12).ToArray();
        return new("hybrid-local-vector", ordered,
            ["Vector similarity uses deterministic local Unicode character features; it is not a claim of semantic understanding.", "Results are source evidence, not accepted facts; inspect each exact revision/range."],
            Version, "query-" + RuntimeJson.TextHash(query)[7..23]);
    }

    private static KnowledgeChunk[] Build(SourceRevision[] sources)
    {
        var chunks = new List<KnowledgeChunk>();
        foreach (var source in sources)
        {
            var start = 0;
            while (start < source.Content.Length)
            {
                var target = Math.Min(source.Content.Length, start + 1200);
                var end = target;
                if (target < source.Content.Length)
                {
                    var boundary = source.Content.LastIndexOfAny(['\n', '.', '!', '?', ';'], target - 1, Math.Min(300, target - start));
                    if (boundary >= start + 500) end = boundary + 1;
                }
                if (end < source.Content.Length && char.IsHighSurrogate(source.Content[end - 1])) end--;
                if (end <= start) end = Math.Min(source.Content.Length, start + 1200);
                var text = source.Content[start..end];
                chunks.Add(new(source, start, end, text, Terms(text), Vector(text)));
                if (end == source.Content.Length) break;
                start = Math.Max(start + 1, end - 120);
                if (start > 0 && start < source.Content.Length && char.IsLowSurrogate(source.Content[start])) start--;
            }
        }
        return chunks.ToArray();
    }

    private static string Fold(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            result.Append(c is 'đ' or 'Đ' ? 'd' : char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return string.Join(' ', result.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string[] Terms(string value) => Fold(value).Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(term => term.Length > 1).Distinct(StringComparer.Ordinal).Take(128).ToArray();

    private static double[] Vector(string value)
    {
        var folded = " " + Fold(value) + " ";
        var result = new double[Dimensions];
        foreach (var term in folded.Split(' ', StringSplitOptions.RemoveEmptyEntries)) Add("w:" + term, 1.7);
        for (var i = 0; i + 2 < folded.Length; i++) Add("g:" + folded.AsSpan(i, 3).ToString(), 1);
        var length = Math.Sqrt(result.Sum(number => number * number));
        if (length > 0) for (var i = 0; i < result.Length; i++) result[i] /= length;
        return result;

        void Add(string feature, double weight)
        {
            ulong hash = 14695981039346656037UL;
            foreach (var value in Encoding.UTF8.GetBytes(feature)) { hash ^= value; hash *= 1099511628211UL; }
            var index = (int)(hash % Dimensions);
            result[index] += (hash & (1UL << 63)) == 0 ? weight : -weight;
        }
    }

    private static double Cosine(double[] left, double[] right)
    {
        double sum = 0;
        for (var i = 0; i < left.Length; i++) sum += left[i] * right[i];
        return Math.Max(0, sum);
    }
}
