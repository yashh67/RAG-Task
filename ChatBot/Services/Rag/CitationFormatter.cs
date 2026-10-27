using System.Text.RegularExpressions;
using ChatBot.Models;

namespace ChatBot.Services.Rag;

/// <summary>
/// Small models often cite by excerpt number ("[1]", "[1, p. 33]", "[1 (Doc, Section, p. 18)]"),
/// add the section ("[Doc, Section, p. 3]"), or repeat a citation in parentheses. This rewrites
/// those forms to "[Document, p. N]" using the excerpts the answer was based on.
/// </summary>
public static partial class CitationFormatter
{
    public static string Normalize(string text, IReadOnlyList<DocumentChunk> excerpts)
    {
        if (excerpts.Count == 0 || !text.Contains('['))
            return text;

        text = ExcerptCitationRegex().Replace(text, match =>
        {
            var numbers = match.Groups["nums"].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(n => int.TryParse(n, out var i) ? i : 0)
                .ToList();
            if (numbers.Any(n => n < 1 || n > excerpts.Count))
                return match.Value; // not an excerpt reference (e.g. a footnote or a year list)

            // Keep a page the model wrote ("p. 33"), else use each excerpt's own location.
            var written = PageRegex().Match(match.Groups["rest"].Value);
            var parts = numbers.Select(n =>
            {
                var chunk = excerpts[n - 1];
                var location = written.Success && numbers.Count == 1 ? written.Value : chunk.Location;
                return $"{chunk.Document}, {location}";
            });
            return "[" + string.Join("; ", parts.Distinct()) + "]";
        });

        foreach (var title in excerpts.Select(e => e.Document).Distinct())
        {
            var escaped = Regex.Escape(title);

            // "[Doc, Section, p. 3]" -> "[Doc, p. 3]"
            text = Regex.Replace(text, $@"\[{escaped},[^\[\];]*?,\s*(?<loc>(?:PDF\s+)?(?:pp?\.|¶)[^\[\];]*)\]", m => $"[{title}, {m.Groups["loc"].Value.Trim()}]");

            // "[Doc, p. 7] (Doc, Use, p. 7)" -> "[Doc, p. 7]"
            text = Regex.Replace(text, $@"(\[{escaped}, [^\[\]]+\])\s*\({escaped},[^()]*\)", "$1");
        }
        return text;
    }

    // "[1]", "[1, 2]", "[1, p. 33]", "[1 (U.S. ..., Section, p. 18)]", "[Excerpt 2]"
    [GeneratedRegex(@"\[(?:excerpts?\s*)?(?<nums>\d{1,2}(?:\s*,\s*\d{1,2})*)(?<rest>(?:\s*,\s*(?:pp?\.|¶)[^\]]*)|(?:\s*\([^\)\]]*\)))?\]", RegexOptions.IgnoreCase)]
    private static partial Regex ExcerptCitationRegex();

    [GeneratedRegex(@"(?:PDF\s+)?(?:pp?\.|¶)\s*[\w–-]+(?:\s*[–-]\s*\w+)?")]
    private static partial Regex PageRegex();
}
