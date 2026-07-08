using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace RagKit.Markdown;

/// <summary>
/// PDF → Markdown via a direct row-grouping heuristic (no ML, no external runtime —
/// a ~80% solution, not a perfect one): extract words, group them into visual rows by
/// Y position (sort + tolerance), sort each row left-to-right, then detect tables by
/// consistent multi-cell X-gaps within a row and headings by font size. Deliberately
/// does NOT use PdfPig's <c>DocstrumBoundingBoxes</c>/<c>UnsupervisedReadingOrderDetector</c>
/// (tried first): that clustering groups a genuinely columnar table by COLUMN rather
/// than by ROW (each column becomes its own block, losing row alignment entirely), and
/// its clustering degrades catastrophically (cubic-ish) on pages with many
/// duplicate/near-duplicate word coordinates — a real-world pattern in scanned/
/// re-flowed PDFs — taking minutes to hours instead of a bounded sort-based pass.
/// Single-column reading order only — a genuinely multi-column page layout (e.g. a
/// two-column article) isn't reconstructed; acceptable given the explicit 80% target
/// and that the documents this targets (invoices, payroll, tax forms) are single-column.
/// </summary>
public static class PdfToMarkdown
{
    private const double HeadingFontSizeRatio = 1.3;
    private const int HeadingMaxChars = 120;
    // Ordinary inter-word reading gaps are a small fraction of the font size (~0.2-0.3x);
    // a genuine column separator is many times that. 2.5x comfortably separates the two
    // without depending on how many words share the row (see SplitIntoCells remarks).
    private const double ColumnGapFontSizeMultiplier = 2.5;

    public static string Convert(string path)
    {
        using var document = PdfDocument.Open(path);
        var sb = new StringBuilder();
        foreach (var page in document.GetPages())
            AppendPage(sb, page);
        return sb.ToString().Trim();
    }

    private static void AppendPage(StringBuilder sb, Page page)
    {
        var letters = page.Letters;
        if (letters.Count == 0) return; // scanned/image-only page: no text layer, nothing to extract

        var words = NearestNeighbourWordExtractor.Instance.GetWords(letters)
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .ToList();
        if (words.Count == 0) return;

        var rows = GroupIntoRows(words);
        double medianFontSize = Median(letters.Select(l => (double)l.PointSize));
        double headingThreshold = medianFontSize * HeadingFontSizeRatio;

        var paragraph = new List<string>();
        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            sb.AppendLine(string.Join(' ', paragraph));
            sb.AppendLine();
            paragraph.Clear();
        }

        var tableRun = new List<List<Word>>();
        foreach (var row in rows)
        {
            double rowFontSize = AverageFontSize(row);
            if (SplitIntoCells(row, rowFontSize * ColumnGapFontSizeMultiplier).Count >= 2)
            {
                FlushParagraph();
                tableRun.Add(row);
                continue;
            }
            FlushTableRun(sb, tableRun);

            var text = string.Join(' ', row.Select(w => w.Text));
            if (text.Length > 0 && text.Length <= HeadingMaxChars && rowFontSize >= headingThreshold)
            {
                FlushParagraph();
                sb.Append("## ").AppendLine(text);
                sb.AppendLine();
            }
            else
            {
                paragraph.Add(text);
            }
        }
        FlushParagraph();
        FlushTableRun(sb, tableRun);
    }

    /// <summary>Groups words into visual rows: sort top-to-bottom, then bucket
    /// consecutive words whose baseline falls within half a word-height of the row's
    /// first word (a cheap, bounded stand-in for real line detection — no clustering
    /// algorithm, so no risk of the pathological blowup <see cref="PdfToMarkdown"/>'s
    /// remarks describe). Each row is then sorted left-to-right.</summary>
    private static List<List<Word>> GroupIntoRows(IReadOnlyList<Word> words)
    {
        var sorted = words.OrderByDescending(w => w.BoundingBox.Bottom).ToList();
        var rows = new List<List<Word>>();
        foreach (var w in sorted)
        {
            var last = rows.Count > 0 ? rows[^1] : null;
            double tolerance = w.BoundingBox.Height * 0.5;
            if (last is not null && Math.Abs(last[0].BoundingBox.Bottom - w.BoundingBox.Bottom) <= tolerance)
                last.Add(w);
            else
                rows.Add(new List<Word> { w });
        }
        foreach (var row in rows)
            row.Sort((a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));
        return rows;
    }

    private static double AverageFontSize(List<Word> row) =>
        Median(row.SelectMany(w => w.Letters).Select(l => (double)l.PointSize));

    /// <summary>Splits a row into cells by horizontal gap: a gap wider than
    /// <paramref name="gapThreshold"/> (a multiple of the row's own font size, NOT a
    /// statistic derived from the row's own gaps) starts a new cell. Deliberately not
    /// self-referential: a 3-column header row has only 2 gaps, both wide — a
    /// per-row-relative median (tried first) is then computed FROM those same wide
    /// gaps and nothing ever clears "3x itself", silently merging real column headers
    /// back into prose. An absolute, font-size-derived threshold has no such trap.</summary>
    private static List<string> SplitIntoCells(List<Word> row, double gapThreshold)
    {
        if (row.Count < 2) return new List<string> { row.Count == 1 ? row[0].Text : "" };

        var cells = new List<string>();
        var current = new StringBuilder(row[0].Text);
        for (int i = 1; i < row.Count; i++)
        {
            double gap = row[i].BoundingBox.Left - row[i - 1].BoundingBox.Right;
            if (gap > gapThreshold)
            {
                cells.Add(current.ToString());
                current = new StringBuilder(row[i].Text);
            }
            else
            {
                current.Append(' ').Append(row[i].Text);
            }
        }
        cells.Add(current.ToString());
        return cells;
    }

    private static void FlushTableRun(StringBuilder sb, List<List<Word>> tableRun)
    {
        if (tableRun.Count == 0) return;
        var rows = tableRun.Select(r => SplitIntoCells(r, AverageFontSize(r) * ColumnGapFontSizeMultiplier)).ToList();
        int colCount = rows.Max(r => r.Count);

        for (int r = 0; r < rows.Count; r++)
        {
            var cells = rows[r].Select(c => c.Replace("|", "\\|"))
                .Concat(Enumerable.Repeat("", Math.Max(0, colCount - rows[r].Count)));
            sb.Append("| ").Append(string.Join(" | ", cells)).AppendLine(" |");
            if (r == 0)
                sb.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", colCount))).AppendLine(" |");
        }
        sb.AppendLine();
        tableRun.Clear();
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
    }
}
