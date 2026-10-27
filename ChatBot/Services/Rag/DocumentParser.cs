using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace ChatBot.Services.Rag;

/// <summary>A heading, paragraph or table row, with where it is in the document.</summary>
public sealed record ParsedBlock(string Text, int LocationStart, int LocationEnd, bool IsHeading = false, bool IsTableRow = false);

public sealed class ParsedDocument
{
    public string? Title { get; set; }

    /// <summary>True for PDFs (locations are pages); false when locations are paragraph numbers.</summary>
    public bool HasPages { get; set; }

    /// <summary>PDF pages or paragraphs.</summary>
    public int LocationCount { get; set; }
    public List<ParsedBlock> Blocks { get; } = new();
    public List<string> Warnings { get; } = new();

    /// <summary>Printed page label for a PDF page number; paragraph numbers are used as-is.</summary>
    public Func<int, string> Label { get; set; } = n => n.ToString();
}

/// <summary>Turns PDF, DOCX, TXT and MD files into clean blocks of text.</summary>
public static partial class DocumentParser
{
    public static readonly string[] SupportedExtensions = [".pdf", ".docx", ".txt", ".md"];

    public static ParsedDocument Parse(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".pdf" => PdfParser.Parse(path),
            ".docx" => ParseDocx(path),
            ".md" => ParsePlainText(path, markdown: true),
            ".txt" => ParsePlainText(path, markdown: false),
            var ext => throw new NotSupportedException($"Unsupported file type '{ext}'."),
        };

    // ---------------------------------------------------------------- shared text helpers

    /// <summary>Joins two lines of the same paragraph, repairing words hyphenated across the break.</summary>
    internal static string JoinLines(string left, string right)
    {
        if (left.Length == 0) return right;
        if (right.Length == 0) return left;
        if (left.Length > 1 && left[^1] is '-' or '­')
        {
            if (char.IsLetter(left[^2]) && char.IsLower(right[0]))
                return left[..^1] + right;              // "publi-" + "cation" => "publication"
            if (char.IsLetterOrDigit(left[^2]))
                return left + right;                    // "University-" + "Jonesboro", "103-" + "40"
        }
        return left + " " + right;
    }

    internal static bool EndsSentence(string text) => SentenceEndRegex().IsMatch(text);

    internal static bool LooksLikeHeadingText(string text)
    {
        if (text.Length is < 2 or > 100 || text.Contains(" | ") || !text.Any(char.IsLetter))
            return false;
        if (char.IsLower(text[0]) || text[^1] is '.' or ',' or ';' or ':' or '-')
            return false;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        // Big-number callouts ("346 kg", "Over 20%", "FY 1980-2018") are not section titles,
        // but numbered headings ("2. Fund Balance", "Box 1.1", "CHAPTER 1") are.
        if (words <= 4 && text.Any(char.IsDigit) && !NumberedHeadingRegex().IsMatch(text))
            return false;
        return words <= 12;
    }

    internal static int WordCount(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    [GeneratedRegex(@"[.!?][""'”’)\]]*$")]
    private static partial Regex SentenceEndRegex();

    [GeneratedRegex(@"^(\d{1,2}(\.\d+)*[.)]?\s+[A-Z]|[A-Z][.)]\s+\S|(chapter|section|part|note|article|annex|appendix|box|table|figure)\s+[A-Z]?[\dIVXLC]+(\.\d+)*[.:]?(\s+\S|$))", RegexOptions.IgnoreCase)]
    internal static partial Regex NumberedHeadingRegex();

    // ---------------------------------------------------------------- DOCX

    private static ParsedDocument ParseDocx(string path)
    {
        using var word = WordprocessingDocument.Open(path, false);
        var result = new ParsedDocument { HasPages = false, Title = NullIfBlank(word.PackageProperties.Title) };
        var body = word.MainDocumentPart?.Document?.Body;
        if (body is null)
            return result;

        // Style IDs can be localized ("Überschrift1"), so look at style names and outline levels too.
        var styles = word.MainDocumentPart!.StyleDefinitionsPart?.Styles?.Elements<Style>()
            .Where(s => s.StyleId?.Value is not null)
            .ToDictionary(s => s.StyleId!.Value!, s => s) ?? new();

        var number = 0;
        void Walk(DocumentFormat.OpenXml.OpenXmlElement parent)
        {
            foreach (var element in parent.ChildElements)
            {
                switch (element)
                {
                    case Paragraph p:
                        var text = ParagraphText(p);
                        if (text.Length == 0) break;
                        number++;
                        result.Blocks.Add(new ParsedBlock(text, number, number, IsHeading: IsWordHeading(p, styles) && text.Length <= 200));
                        break;
                    case Table table:
                        foreach (var row in table.Elements<TableRow>())
                        {
                            var cells = row.Elements<TableCell>()
                                .Select(c => string.Join(" ", c.Elements<Paragraph>().Select(ParagraphText).Where(t => t.Length > 0)))
                                .ToList();
                            if (cells.All(c => c.Length == 0)) continue;
                            number++;
                            result.Blocks.Add(new ParsedBlock(string.Join(" | ", cells), number, number, IsTableRow: true));
                        }
                        break;
                    default:
                        Walk(element); // content controls, custom XML, etc.
                        break;
                }
            }
        }

        Walk(body);
        result.LocationCount = number;
        return result;
    }

    private static string ParagraphText(Paragraph p)
    {
        var sb = new StringBuilder();
        foreach (var run in p.Descendants<Run>())
        {
            foreach (var child in run.ChildElements)
            {
                switch (child)
                {
                    case Text t: sb.Append(t.Text); break;
                    case TabChar or Break or CarriageReturn: sb.Append(' '); break;
                }
            }
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static bool IsWordHeading(Paragraph p, Dictionary<string, Style> styles)
    {
        if (p.ParagraphProperties?.OutlineLevel?.Val?.Value is int level && level < 9)
            return true;

        // Follow the style and its basedOn chain.
        var styleId = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        for (var depth = 0; styleId is not null && depth < 5; depth++)
        {
            if (!styles.TryGetValue(styleId, out var style))
                return styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase);
            var name = style.StyleName?.Val?.Value ?? styleId;
            if (name.StartsWith("heading", StringComparison.OrdinalIgnoreCase) || name.Equals("Title", StringComparison.OrdinalIgnoreCase))
                return true;
            if (style.StyleParagraphProperties?.OutlineLevel?.Val?.Value is int styleLevel && styleLevel < 9)
                return true;
            styleId = style.BasedOn?.Val?.Value;
        }
        return false;
    }

    // ---------------------------------------------------------------- TXT / MD

    private static ParsedDocument ParsePlainText(string path, bool markdown)
    {
        string content;
        using (var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            content = reader.ReadToEnd();

        var result = new ParsedDocument { HasPages = false };
        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new List<string>();
        var number = 0;
        var inCode = false;

        void Flush()
        {
            if (paragraph.Count == 0) return;
            number++;
            var text = inCode
                ? string.Join("\n", paragraph)
                : paragraph.Aggregate("", (acc, l) => IsListItem(l) && acc.Length > 0 ? acc + "\n" + l : JoinLines(acc, l));
            var heading = !markdown && !inCode && paragraph.Count == 1 && IsPlainTextHeading(text);
            result.Blocks.Add(new ParsedBlock(text, number, number, IsHeading: heading));
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd();
            var line = raw.Trim();

            if (markdown && line.StartsWith("```"))
            {
                if (inCode) { paragraph.Add(raw); Flush(); inCode = false; }
                else { Flush(); inCode = true; paragraph.Add(raw); }
                continue;
            }
            if (inCode) { paragraph.Add(raw); continue; }

            if (line.Length == 0) { Flush(); continue; }

            // Markdown "# Heading" and setext / underlined headings ("Title" followed by "=====").
            var underline = i + 1 < lines.Length && Regex.IsMatch(lines[i + 1].Trim(), @"^(=+|-+)$") && lines[i + 1].Trim().Length >= 3;
            if ((markdown && line.StartsWith('#')) || (underline && paragraph.Count == 0 && line.Length <= 100))
            {
                Flush();
                number++;
                result.Blocks.Add(new ParsedBlock(line.TrimStart('#').Trim(), number, number, IsHeading: true));
                if (underline) i++;
                continue;
            }

            // Table rows (Markdown pipes) stay one per line; separator rows like |---|---| are dropped.
            if (line.StartsWith('|') && line.Count(c => c == '|') >= 2)
            {
                Flush();
                if (Regex.IsMatch(line, @"^\|[\s:|-]+\|?$")) continue;
                number++;
                var cells = line.Trim('|').Split('|').Select(c => c.Trim());
                result.Blocks.Add(new ParsedBlock(string.Join(" | ", cells), number, number, IsTableRow: true));
                continue;
            }

            paragraph.Add(line);
        }
        Flush();

        if (markdown && result.Blocks.FirstOrDefault(b => b.IsHeading) is { } first)
            result.Title = first.Text;
        result.LocationCount = number;
        return result;
    }

    private static bool IsListItem(string line) => Regex.IsMatch(line, @"^([-*+]|\d+[.)])\s");

    private static bool IsPlainTextHeading(string text)
    {
        if (!LooksLikeHeadingText(text) || text.Length > 80)
            return false;
        var letters = text.Where(char.IsLetter).ToList();
        var allCaps = letters.Count >= 3 && letters.All(char.IsUpper);
        return allCaps || NumberedHeadingRegex().IsMatch(text);
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ---------------------------------------------------------------- PDF

    private static class PdfParser
    {
        /// <summary>Text on one line separated from its neighbours by a wide gap (a table cell or a column).</summary>
        private sealed class Segment
        {
            public StringBuilder Text { get; } = new();
            public double X { get; init; }
            public double Right { get; set; }
        }

        /// <summary>A line of text rebuilt from letter positions.</summary>
        private sealed class Line
        {
            public List<Segment> Segments { get; init; } = new();
            public string Text { get; set; } = "";
            public double X => Segments[0].X;
            public double Y { get; init; }          // baseline, PDF coordinates (0 = bottom)
            public double Size { get; set; }
            public string Font { get; set; } = "";
            public double FontShare { get; set; } = 1;  // share of letters in the main font
            public int Page { get; init; }
            public bool IsTocEntry { get; set; }

            /// <summary>Bold lead-in at the start of a paragraph ("History  From the Mayflower...").</summary>
            public string? RunInHeading { get; set; }
        }

        private sealed record BodyStyle(string Font, double Size);

        public static ParsedDocument Parse(string path)
        {
            using var pdf = PdfDocument.Open(path);
            var result = new ParsedDocument { HasPages = true, LocationCount = pdf.NumberOfPages };

            var title = NullIfBlank(pdf.Information.Title);
            if (title is not null && !title.StartsWith("Microsoft Word", StringComparison.OrdinalIgnoreCase) && !title.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                result.Title = title;

            // Pass 1: lines per page, plus header/footer candidates.
            var pages = new List<(Page Page, List<Line> Lines)>();
            var scannedPages = 0;
            foreach (var page in pdf.GetPages())
            {
                var lines = BuildLines(page);
                pages.Add((page, lines));
                if (lines.Count == 0 && page.GetImages().Any())
                    scannedPages++;
            }

            if (scannedPages > 0)
            {
                result.Warnings.Add(scannedPages == pages.Count
                    ? "No extractable text: this looks like a scanned PDF (OCR is not supported)."
                    : $"{scannedPages} of {pages.Count} pages have no extractable text (likely scanned; no OCR).");
            }

            // Lines in the top/bottom margin that repeat on several pages are headers/footers.
            var marginKeyPages = new Dictionary<string, HashSet<int>>();
            foreach (var (page, lines) in pages)
            {
                foreach (var line in lines.Where(l => InMargin(l, page)))
                {
                    var key = MarginKey(line.Text);
                    if (key.Length == 0) continue;
                    if (!marginKeyPages.TryGetValue(key, out var set))
                        marginKeyPages[key] = set = new HashSet<int>();
                    set.Add(page.Number);
                }
            }
            bool IsRepeated(Line l) => marginKeyPages.TryGetValue(MarginKey(l.Text), out var s) && s.Count >= 3;

            var pageNumberCandidates = new Dictionary<int, List<int>>();
            var docBody = DocumentBody(pages.SelectMany(p => p.Lines));

            // Pass 2: drop headers/footers and page numbers, then build blocks across pages.
            var builder = new BlockBuilder(result);
            foreach (var (page, lines) in pages)
            {
                var kept = new List<Line>();
                var candidates = new List<int>();
                foreach (var line in lines)
                {
                    if (InMargin(line, page))
                    {
                        var bare = BarePageNumberRegex().Match(line.Text);
                        if (bare.Success) { candidates.Add(int.Parse(bare.Value)); continue; }
                        if (IsRepeated(line))
                        {
                            var edge = EdgePageNumberRegex().Match(line.Text);
                            if (edge.Success) candidates.Add(int.Parse(edge.Groups[1].Success ? edge.Groups[1].Value : edge.Groups[2].Value));
                            continue;
                        }
                    }
                    kept.Add(line);
                }
                pageNumberCandidates[page.Number] = candidates;

                var body = PageBody(kept) ?? docBody;
                foreach (var line in kept)
                    builder.Add(line, IsHeading(line, body), page.Width);
            }
            builder.Flush();

            result.Label = PrintedPageLabels(pageNumberCandidates, pages.Count);
            return result;
        }

        /// <summary>
        /// Rebuilds lines from letters in content-stream order (which usually follows the reading
        /// order of columns). This PDF has no space characters, so spaces come from gaps: measured
        /// word gaps are 0.25–0.5 em while letters inside words (even letter-spaced headings) sit
        /// within 0.13 em. Gaps over 2 em separate table cells or columns.
        /// </summary>
        private static List<Line> BuildLines(Page page)
        {
            var lines = new List<Line>();
            var lineLetters = new List<(Letter Letter, int Segment, int Index)>();
            Line? current = null;
            double lastEnd = 0;
            var pendingSpace = false;

            void Close()
            {
                if (current is null) return;
                SplitRunInHeading(current, lineLetters);
                var letters = lineLetters.Select(l => l.Letter).ToList();
                current.Size = Math.Round(letters.Select(l => l.PointSize).OrderBy(s => s).ElementAt(letters.Count / 2), 1);
                var fonts = letters.GroupBy(l => l.FontName ?? "").OrderByDescending(g => g.Count()).First();
                current.Font = fonts.Key;
                current.FontShare = fonts.Count() / (double)letters.Count;
                lines.Add(current);
                lineLetters.Clear();
                current = null;
            }

            Letter? previous = null;
            foreach (var letter in page.Letters)
            {
                if (letter.TextOrientation != TextOrientation.Horizontal)
                    continue;
                if (string.IsNullOrWhiteSpace(letter.Value))
                {
                    // Real space characters (where the PDF has them) always count as a word break.
                    pendingSpace = current is not null;
                    continue;
                }
                // Some PDFs fake bold by drawing every glyph twice at the same spot.
                if (previous is not null && previous.Value == letter.Value
                    && Math.Abs(previous.StartBaseLine.X - letter.StartBaseLine.X) < 0.3
                    && Math.Abs(previous.StartBaseLine.Y - letter.StartBaseLine.Y) < 0.3)
                    continue;
                previous = letter;

                var size = letter.PointSize > 0.5 ? letter.PointSize : Math.Max(letter.BoundingBox.Height, 1);
                var x = letter.StartBaseLine.X;
                var y = letter.StartBaseLine.Y;

                // Footnote markers ("content.¹²") are small, raised digits: drop them so they don't
                // stick to words ("chemistry5") or break the line.
                if (current is not null && IsFootnoteMark(letter.Value) && size < 0.8 * current.Size
                    && y - current.Y > 0.1 * current.Size && y - current.Y < 0.9 * current.Size && x >= lastEnd - 0.5 * current.Size)
                {
                    lastEnd = letter.EndBaseLine.X;
                    continue;
                }

                if (current is not null && Math.Abs(y - current.Y) <= 0.5 * size && x >= lastEnd - 0.5 * size)
                {
                    var gap = x - lastEnd;
                    if (gap > 2.0 * size)
                        current.Segments.Add(new Segment { X = x });
                    else if (pendingSpace || gap > 0.2 * size)
                        current.Segments[^1].Text.Append(' ');
                }
                else
                {
                    Close();
                    // Size starts as the first letter's size (used for the footnote check), and is
                    // replaced by the line's median size when the line closes.
                    current = new Line { Y = y, Page = page.Number, Size = size, Segments = { new Segment { X = x } } };
                }
                pendingSpace = false;

                var segment = current.Segments[^1];
                lineLetters.Add((letter, current.Segments.Count - 1, segment.Text.Length));
                segment.Text.Append(letter.Value);
                segment.Right = letter.EndBaseLine.X;
                lastEnd = letter.EndBaseLine.X;
            }
            Close();

            var ordered = FixInterleavedColumns(lines);
            foreach (var line in ordered)
            {
                var text = string.Join(" | ", line.Segments.Select(s => s.Text.ToString().Trim()).Where(t => t.Length > 0));
                text = text.Normalize(NormalizationForm.FormKC);     // ligatures: "ﬁgure" -> "figure"
                line.IsTocEntry = LeaderRegex().IsMatch(text);
                text = LeaderRegex().Replace(text, " ");            // table-of-contents dot leaders
                line.Text = text.Replace("$ | ", "$ ").Trim();
            }
            return ordered.Where(l => l.Text.Length > 0 && !IsChartAxis(l.Text)).ToList();
        }

        private static bool IsFootnoteMark(string value) =>
            value.Length == 1 && (char.IsDigit(value[0]) || value[0] is ',' or '*' or '†' or '‡');

        /// <summary>
        /// Several numbers and no words, outside a table ("-0.4 -0.2 0.0 0.2", "2014 2015 2016"):
        /// chart axis labels, which only add noise. Single numbers are kept (page numbers, callouts).
        /// </summary>
        private static bool IsChartAxis(string text) =>
            !text.Any(char.IsLetter) && !text.Contains(" | ") && text.Contains(' ');

        /// <summary>
        /// A line that starts in bold and then switches to regular text for good is a run-in
        /// heading. The bold part becomes the heading and is removed from the line (and from the
        /// letter list, so the line's font is the body font).
        /// </summary>
        private static void SplitRunInHeading(Line line, List<(Letter Letter, int Segment, int Index)> letters)
        {
            var split = letters.FindIndex(l => !IsBold(l.Letter.FontName ?? ""));
            if (split < 3 || letters.Count - split < 10 || letters[split].Segment != 0)
                return;
            if (letters.Skip(split).Count(l => IsBold(l.Letter.FontName ?? "")) > 2)
                return;

            var text = line.Segments[0].Text.ToString();
            var heading = text[..letters[split].Index].Trim().TrimEnd('.', ':', '–', '-', '—').Trim();
            if (!LooksLikeHeadingText(heading))
                return;

            line.RunInHeading = heading;
            line.Segments[0].Text.Clear().Append(text[letters[split].Index..].TrimStart());
            letters.RemoveRange(0, split);
        }

        /// <summary>
        /// Some PDFs draw two text columns row by row, so one baseline holds text from both columns.
        /// When several lines have two long text segments, split them at the gutter and read the
        /// left column before the right one.
        /// </summary>
        private static List<Line> FixInterleavedColumns(List<Line> lines)
        {
            static bool IsProse(Segment s) => s.Text.Length >= 20 && s.Text.ToString().Count(c => c == ' ') >= 2;
            var twoColumn = lines.Where(l => l.Segments.Count == 2 && l.Segments.All(IsProse)).ToList();
            if (twoColumn.Count < 4)
                return lines;

            var gutters = twoColumn.Select(l => (l.Segments[0].Right + l.Segments[1].X) / 2).OrderBy(g => g).ToList();
            var split = gutters[gutters.Count / 2];

            var left = new List<Line>();
            var right = new List<Line>();
            var full = new List<Line>();
            foreach (var line in lines)
            {
                var l = line.Segments.Where(s => s.Right <= split + 2).ToList();
                var r = line.Segments.Where(s => s.X >= split - 2).ToList();
                if (l.Count + r.Count != line.Segments.Count)
                {
                    full.Add(line);                                     // spans the gutter (title, wide table)
                    continue;
                }
                if (l.Count > 0) left.Add(new Line { Y = line.Y, Page = line.Page, Size = line.Size, Font = line.Font, Segments = l });
                if (r.Count > 0) right.Add(new Line { Y = line.Y, Page = line.Page, Size = line.Size, Font = line.Font, Segments = r });
            }

            var top = left.Concat(right).Max(l => l.Y);
            return full.Where(l => l.Y > top)
                .Concat(left.OrderByDescending(l => l.Y))
                .Concat(right.OrderByDescending(l => l.Y))
                .Concat(full.Where(l => l.Y <= top).OrderByDescending(l => l.Y))
                .ToList();
        }

        private static bool InMargin(Line line, Page page) => line.Y > page.Height * 0.88 || line.Y < page.Height * 0.12;

        /// <summary>Header/footer identity, ignoring digits (page numbers) and spacing.</summary>
        private static string MarginKey(string text) => new(text.ToLowerInvariant().Where(char.IsLetter).ToArray());

        private static BodyStyle? PageBody(List<Line> lines)
        {
            var chars = lines.Sum(l => l.Text.Length);
            return chars < 400 ? null : DocumentBody(lines);
        }

        private static BodyStyle DocumentBody(IEnumerable<Line> lines)
        {
            var best = lines.GroupBy(l => (l.Font, l.Size))
                .Select(g => (g.Key, Chars: g.Sum(l => l.Text.Length)))
                .OrderByDescending(g => g.Chars)
                .FirstOrDefault();
            return best.Chars == 0 ? new BodyStyle("", 10) : new BodyStyle(best.Key.Font, best.Key.Size);
        }

        private static bool IsBold(string font)
        {
            var f = font.ToLowerInvariant();
            return f.Contains("bold") || f.Contains("black") || f.Contains("heavy") || f.Contains("semibold")
                || f.Contains("demi") || f.Contains("medium") || f.EndsWith("-md") || f.EndsWith(",bold");
        }

        private static bool IsHeading(Line line, BodyStyle body)
        {
            if (line.IsTocEntry || line.FontShare < 0.9 || !LooksLikeHeadingText(line.Text) || line.Size < body.Size - 0.3)
                return false;
            var larger = line.Size >= body.Size * 1.15;
            var bolder = IsBold(line.Font) && !IsBold(body.Font);
            return larger || bolder || (line.Size > body.Size + 0.1 && NumberedHeadingRegex().IsMatch(line.Text));
        }

        /// <summary>Groups lines into headings, table rows and paragraphs (paragraphs may continue onto the next page).</summary>
        private sealed class BlockBuilder(ParsedDocument result)
        {
            private string _text = "";
            private int _start, _end;
            private Line? _prev;
            private bool _prevWasHeading, _prevWasRow;

            public void Add(Line line, bool heading, double pageWidth)
            {
                if (line.RunInHeading is not null)
                {
                    Flush();
                    result.Blocks.Add(new ParsedBlock(line.RunInHeading, line.Page, line.Page, IsHeading: true));
                    _start = line.Page;
                    _text = line.Text;
                    _end = line.Page;
                    Remember(line, heading: false, row: false);
                    return;
                }

                // A heading wrapped over two lines, in the same style, right below the first line.
                var continuesHeading = _prevWasHeading && _prev is not null && _prev.Page == line.Page && _prev.Font == line.Font
                    && Math.Abs(_prev.Size - line.Size) < 0.2 && _prev.Y - line.Y > 0 && _prev.Y - line.Y < 1.8 * line.Size
                    && !line.Text.Contains(" | ") && result.Blocks.Count > 0 && result.Blocks[^1].IsHeading;
                if (continuesHeading)
                {
                    var last = result.Blocks[^1];
                    var merged = JoinLines(last.Text, line.Text);

                    // Not a heading after all, but the first line of a paragraph set in a larger or
                    // bolder font (intro text, boxes): the next line finishes the sentence, or the
                    // "title" grows too long.
                    if ((char.IsLower(line.Text[0]) && EndsSentence(line.Text)) || WordCount(merged) > 15)
                    {
                        result.Blocks.RemoveAt(result.Blocks.Count - 1);
                        _start = last.LocationStart;
                        _text = merged;
                        _end = line.Page;
                        Remember(line, heading: false, row: false);
                        return;
                    }
                    if (heading || !EndsSentence(line.Text))
                    {
                        result.Blocks[^1] = last with { Text = merged };   // a heading wrapped over two lines
                        Remember(line, heading: true, row: false);
                        return;
                    }
                }

                if (heading)
                {
                    Flush();
                    result.Blocks.Add(new ParsedBlock(line.Text, line.Page, line.Page, IsHeading: true));
                    Remember(line, heading: true, row: false);
                    return;
                }

                if (line.Text.Contains(" | "))
                {
                    Flush();
                    result.Blocks.Add(new ParsedBlock(line.Text, line.Page, line.Page, IsTableRow: true));
                    Remember(line, heading: false, row: true);
                    return;
                }

                if (_text.Length > 0 && StartsNewParagraph(line, pageWidth))
                    Flush();

                if (_text.Length == 0)
                    _start = line.Page;
                _text = JoinLines(_text, line.Text);
                _end = line.Page;
                Remember(line, heading: false, row: false);
            }

            private bool StartsNewParagraph(Line line, double pageWidth)
            {
                if (_prev is null || _prevWasHeading || _prevWasRow)
                    return true;

                var size = Math.Max(line.Size, _prev.Size);
                var samePage = line.Page == _prev.Page;
                var down = samePage ? _prev.Y - line.Y : 0;
                var sameColumn = samePage && down > 0 && Math.Abs(line.X - _prev.X) < pageWidth * 0.25;
                var endsSentence = EndsSentence(_prev.Text);

                if (sameColumn && down > 1.6 * size)
                    return true;                                    // blank line between paragraphs
                if (Math.Abs(line.Size - _prev.Size) > 1.0)
                    return true;                                    // e.g. body text -> caption
                if (!endsSentence)
                    return false;                                   // sentence continues (also across columns/pages)
                var indented = sameColumn && line.X > _prev.X + 0.8 * size;
                return indented || !sameColumn || down > 1.3 * size;
            }

            private void Remember(Line line, bool heading, bool row)
            {
                _prev = line;
                _prevWasHeading = heading;
                _prevWasRow = row;
            }

            public void Flush()
            {
                if (_text.Length > 0)
                    result.Blocks.Add(new ParsedBlock(_text, _start, _end));
                _text = "";
            }
        }

        /// <summary>
        /// Maps PDF page numbers to printed page numbers. Each page's candidates come from its
        /// header/footer; a candidate is trusted when its offset (PDF page - printed page) agrees
        /// with other pages. Pages without a number get the offset of the nearest numbered page.
        /// </summary>
        private static Func<int, string> PrintedPageLabels(Dictionary<int, List<int>> candidates, int pageCount)
        {
            var offsetVotes = candidates.Where(c => c.Value.Count == 1)
                .GroupBy(c => c.Key - c.Value[0])
                .ToDictionary(g => g.Key, g => g.Count());

            var offsets = new Dictionary<int, int>();
            foreach (var (page, values) in candidates)
            {
                var good = values.Where(v => v >= 1 && offsetVotes.GetValueOrDefault(page - v) >= 2).ToList();
                if (good.Count == 0) continue;
                // Several candidates (e.g. a letter's own "2" and the report's "24"): pick the most-voted offset.
                offsets[page] = good.Select(v => page - v).OrderByDescending(o => offsetVotes[o]).First();
            }

            if (offsets.Count == 0)
                return n => n.ToString(); // no printed numbers found: use PDF page numbers

            return pdfPage =>
            {
                if (!offsets.TryGetValue(pdfPage, out var offset))
                {
                    var nearest = offsets.Keys.OrderBy(p => Math.Abs(p - pdfPage)).ThenBy(p => p > pdfPage).First();
                    offset = offsets[nearest];
                }
                var printed = pdfPage - offset;
                return printed >= 1 ? printed.ToString() : $"PDF {pdfPage}";
            };
        }
    }

    [GeneratedRegex(@"^\d{1,3}$")]
    private static partial Regex BarePageNumberRegex();

    [GeneratedRegex(@"\s*[.…·_]{4,}\s*")]
    private static partial Regex LeaderRegex();

    // "6 2018 ANNUAL REPORT", "5U.S. GOVERNMENT...", "...ANNUAL REPORT 25" (but not "2018 ANNUAL...")
    [GeneratedRegex(@"^(\d{1,3})(?!\d)(?=\s*\D)|(?<=\D)(\d{1,3})$")]
    private static partial Regex EdgePageNumberRegex();
}
