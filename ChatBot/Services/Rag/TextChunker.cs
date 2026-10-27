using System.Text;
using System.Text.RegularExpressions;

namespace ChatBot.Services.Rag;

public sealed record TextChunk(string Section, int LocationStart, int LocationEnd, string Text);

/// <summary>
/// Splits a parsed document by section, then paragraph, then sentence into chunks of about
/// <c>maxTokens</c>, with about <c>overlapTokens</c> of trailing sentences repeated at the start
/// of the next chunk. Sentences are never cut (only a single sentence longer than a whole chunk is split).
/// </summary>
public static partial class TextChunker
{
    private sealed record Unit(string Text, int Paragraph, int LocationStart, int LocationEnd, int Tokens);

    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr.", "mrs.", "ms.", "dr.", "st.", "no.", "nos.", "vs.", "e.g.", "i.e.", "etc.", "inc.", "co.", "corp.",
        "ltd.", "jr.", "sr.", "u.s.", "u.k.", "jan.", "feb.", "mar.", "apr.", "aug.", "sep.", "sept.", "oct.",
        "nov.", "dec.", "fig.", "sec.", "vol.", "pp.", "p.", "approx.", "dept.", "gov.", "u.s.c.",
    };

    public static List<TextChunk> Chunk(ParsedDocument doc, Func<string, int> countTokens, int maxTokens, int overlapTokens)
    {
        var chunks = new List<TextChunk>();
        var section = "";
        var lastHeading = "";
        var units = new List<Unit>();
        var paragraph = 0;
        var lastWasHeading = false;

        foreach (var block in doc.Blocks)
        {
            if (block.IsHeading)
            {
                if (units.Count > 0)
                {
                    Pack(section, units, chunks, maxTokens, overlapTokens);
                    units.Clear();
                }
                // Consecutive headings ("Selected Balance Sheet Accounts" then "Cash") read as a
                // path, at most two levels deep (chart panel titles can come in long runs).
                section = lastWasHeading && lastHeading.Length > 0 ? $"{lastHeading} › {block.Text}" : block.Text;
                lastHeading = block.Text;
                lastWasHeading = true;
                continue;
            }

            lastWasHeading = false;
            paragraph++;
            var pieces = block.IsTableRow ? [block.Text] : SplitSentences(block.Text);
            foreach (var sentence in pieces)
            {
                var tokens = countTokens(sentence);
                if (tokens <= maxTokens)
                {
                    units.Add(new Unit(sentence, paragraph, block.LocationStart, block.LocationEnd, tokens));
                    continue;
                }
                // A single "sentence" longer than a chunk (huge table cell, text without punctuation).
                foreach (var part in SplitByWords(sentence, countTokens, maxTokens))
                    units.Add(new Unit(part, paragraph, block.LocationStart, block.LocationEnd, countTokens(part)));
            }
        }
        Pack(section, units, chunks, maxTokens, overlapTokens);
        return chunks;
    }

    private static void Pack(string section, List<Unit> units, List<TextChunk> chunks, int maxTokens, int overlapTokens)
    {
        var current = new List<Unit>();
        var tokens = 0;

        void Emit()
        {
            chunks.Add(new TextChunk(section, current.Min(u => u.LocationStart), current.Max(u => u.LocationEnd), Render(current)));

            // Carry trailing sentences (up to the overlap budget) into the next chunk.
            var tail = new List<Unit>();
            var tailTokens = 0;
            for (var i = current.Count - 1; i > 0; i--)
            {
                if (tailTokens + current[i].Tokens > overlapTokens) break;
                tail.Insert(0, current[i]);
                tailTokens += current[i].Tokens;
            }
            current = tail;
            tokens = tailTokens;
        }

        for (var i = 0; i < units.Count; i++)
        {
            var unit = units[i];

            // Prefer to break between paragraphs when the next paragraph won't fit and the chunk is already decent.
            var startsParagraph = i == 0 || units[i - 1].Paragraph != unit.Paragraph;
            if (startsParagraph && current.Count > 0 && tokens >= maxTokens / 2)
            {
                var paragraphTokens = units.Skip(i).TakeWhile(u => u.Paragraph == unit.Paragraph).Sum(u => u.Tokens);
                if (tokens + paragraphTokens > maxTokens)
                    Emit();
            }

            if (tokens + unit.Tokens > maxTokens && current.Count > 0)
            {
                Emit();
                // The overlap itself might not leave room for this sentence.
                while (current.Count > 0 && tokens + unit.Tokens > maxTokens)
                {
                    tokens -= current[0].Tokens;
                    current.RemoveAt(0);
                }
            }

            current.Add(unit);
            tokens += unit.Tokens;
        }

        // Skip a final chunk that is only overlap from the previous one.
        if (current.Count > 0 && (chunks.Count == 0 || !chunks[^1].Text.EndsWith(Render(current), StringComparison.Ordinal)))
            chunks.Add(new TextChunk(section, current.Min(u => u.LocationStart), current.Max(u => u.LocationEnd), Render(current)));
    }

    /// <summary>Sentences of a paragraph are joined by spaces; paragraphs and table rows by new lines.</summary>
    private static string Render(List<Unit> units)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < units.Count; i++)
        {
            if (i > 0)
                sb.Append(units[i].Paragraph == units[i - 1].Paragraph ? ' ' : '\n');
            sb.Append(units[i].Text);
        }
        return sb.ToString();
    }

    public static List<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        var start = 0;
        foreach (Match m in SentenceBreakRegex().Matches(text))
        {
            var candidate = text[start..m.Index];
            var lastWord = candidate[(candidate.LastIndexOf(' ') + 1)..];
            // "U.S. Government", "John W. Crawford", "e.g. this" are not sentence ends.
            if (Abbreviations.Contains(lastWord) || InitialRegex().IsMatch(lastWord))
                continue;
            sentences.Add(candidate.Trim());
            start = m.Index + m.Length;
        }
        if (start < text.Length)
            sentences.Add(text[start..].Trim());
        return sentences.Where(s => s.Length > 0).ToList();
    }

    private static IEnumerable<string> SplitByWords(string text, Func<string, int> countTokens, int maxTokens)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var word in words)
        {
            var next = sb.Length == 0 ? word : sb + " " + word;
            if (sb.Length > 0 && countTokens(next) > maxTokens)
            {
                yield return sb.ToString();
                sb.Clear().Append(word);
            }
            else
            {
                sb.Clear().Append(next);
            }
        }
        if (sb.Length > 0)
            yield return sb.ToString();
    }

    // Whitespace after . ! ? (plus closing quotes/brackets), followed by something that can start a sentence.
    [GeneratedRegex(@"(?<=[.!?][""'”’)\]]*)\s+(?=[""'“‘(\[]?[A-Z0-9$])")]
    private static partial Regex SentenceBreakRegex();

    [GeneratedRegex(@"^([A-Z]\.)+$")]
    private static partial Regex InitialRegex();
}
