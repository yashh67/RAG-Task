using System.Text.RegularExpressions;
using ChatBot.Models;
using Microsoft.Extensions.Options;

namespace ChatBot.Services.Rag;

/// <summary>Hybrid search: vector (cosine) top-k + BM25 top-k, merged with Reciprocal Rank Fusion.</summary>
public sealed class Retriever
{
    private const int RrfK = 60;

    private readonly DocumentLibrary _library;
    private readonly EmbeddingService _embedder;
    private readonly RagOptions _options;

    public Retriever(DocumentLibrary library, EmbeddingService embedder, IOptions<RagOptions> options)
    {
        _library = library;
        _embedder = embedder;
        _options = options.Value;
    }

    /// <summary>Best chunks for the query, best first (at most FinalTopK).</summary>
    public async Task<IReadOnlyList<DocumentChunk>> SearchAsync(string query, string? documentId, CancellationToken ct)
    {
        var chunks = _library.GetChunks(documentId).Where(c => !c.IsTiny).ToList();
        if (chunks.Count == 0)
            return [];

        var queryVector = await _embedder.EmbedQueryAsync(query, ct);

        // Vectors are L2-normalized, so the dot product is the cosine similarity.
        var byVector = chunks
            .Select(c => (Chunk: c, Score: Dot(queryVector, c.Embedding)))
            .OrderByDescending(x => x.Score)
            .Take(_options.VectorTopK)
            .Select(x => x.Chunk)
            .ToList();

        var byKeyword = KeywordIndex.Search(query, chunks, _options.KeywordTopK);

        var fused = new Dictionary<DocumentChunk, double>();
        foreach (var list in new[] { byVector, byKeyword })
            for (var rank = 0; rank < list.Count; rank++)
                fused[list[rank]] = fused.GetValueOrDefault(list[rank]) + 1.0 / (RrfK + rank + 1);

        return fused.OrderByDescending(x => x.Value).Take(_options.FinalTopK).Select(x => x.Key).ToList();
    }

    private static float Dot(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        float sum = 0;
        for (var i = 0; i < a.Length; i++)
            sum += a[i] * b[i];
        return sum;
    }
}

/// <summary>A small BM25 keyword index. Statistics are computed over the searched chunks at query time.</summary>
public static partial class KeywordIndex
{
    private const double K1 = 1.2, B = 0.75;

    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "has", "have", "how", "in", "is", "it",
        "its", "of", "on", "or", "that", "the", "this", "to", "was", "were", "what", "when", "where", "which",
        "who", "why", "will", "with", "did", "does", "do", "can", "you", "me", "about", "tell", "many", "much",
    ];

    /// <summary>Fills in the chunk's term counts (after loading from cache or indexing).</summary>
    public static void Prepare(DocumentChunk chunk)
    {
        var terms = Tokenize($"{chunk.Section} {chunk.Text}").ToList();
        chunk.Terms = terms.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
        chunk.TermCount = terms.Count;
        chunk.IsTiny = chunk.Text.Count(char.IsLetter) < 30;
    }

    public static List<DocumentChunk> Search(string query, IReadOnlyList<DocumentChunk> chunks, int topK)
    {
        var queryTerms = Tokenize(query).Distinct().ToList();
        if (queryTerms.Count == 0 || chunks.Count == 0)
            return [];

        var averageLength = chunks.Average(c => (double)c.TermCount);
        var idf = queryTerms.ToDictionary(t => t, t =>
        {
            var df = chunks.Count(c => c.Terms.ContainsKey(t));
            return Math.Log(1 + (chunks.Count - df + 0.5) / (df + 0.5));
        });

        return chunks
            .Select(c => (Chunk: c, Score: queryTerms.Sum(t =>
            {
                if (!c.Terms.TryGetValue(t, out var tf)) return 0;
                return idf[t] * tf * (K1 + 1) / (tf + K1 * (1 - B + B * c.TermCount / averageLength));
            })))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => x.Chunk)
            .ToList();
    }

    /// <summary>Lower-case words and numbers; keeps "5,014,573", "u.s." and "fy" together.</summary>
    private static IEnumerable<string> Tokenize(string text) =>
        TermRegex().Matches(text.ToLowerInvariant())
            .Select(m => m.Value.Trim('.', ','))
            .Where(t => t.Length > 1 && !StopWords.Contains(t) || t.Length == 1 && char.IsDigit(t[0]));

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:[.,'’-][\p{L}\p{N}]+)*")]
    private static partial Regex TermRegex();
}
