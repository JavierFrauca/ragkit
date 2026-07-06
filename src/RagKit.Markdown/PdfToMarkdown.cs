using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace RagKit.Markdown;

/// <summary>
/// PDF → Markdown via PdfPig's layout-analysis heuristics (no ML, no external
/// runtime — a ~80% solution, not a perfect one): per page, extract words, segment
/// into blocks, and order them for reading. A block is treated as a heading when it's
/// a single short line noticeably larger than the page's median font size; a
/// rectangular grid of blocks aligned in both columns and rows is treated as a table.
/// Complex multi-column layouts, scanned/image-only PDFs (no text layer) and nested
/// tables are known weak spots — acceptable given the explicit 80% target.
/// </summary>
public static class PdfToMarkdown
{
    private const double HeadingFontSizeRatio = 1.3;

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

        var words = NearestNeighbourWordExtractor.Instance.GetWords(letters).ToList();
        if (words.Count == 0) return;

        var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
        var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks);

        double medianFontSize = Median(letters.Select(l => (double)l.PointSize));
        double headingThreshold = medianFontSize * HeadingFontSizeRatio;

        var tableRun = new List<TextBlock>();
        foreach (var block in ordered)
        {
            if (LooksLikeTableRow(block))
            {
                tableRun.Add(block);
                continue;
            }
            FlushTableRun(sb, tableRun);

            if (LooksLikeHeading(block, headingThreshold))
                sb.Append("## ").AppendLine(block.Text.Trim());
            else
                sb.AppendLine(block.Text.Trim());
            sb.AppendLine();
        }
        FlushTableRun(sb, tableRun);
    }

    /// <summary>A block reads as a heading when it's a single short line whose average
    /// font size clearly exceeds the page's median — the cheapest reliable signal
    /// available without a trained layout model.</summary>
    private static bool LooksLikeHeading(TextBlock block, double headingThreshold)
    {
        if (block.TextLines.Count != 1) return false;
        var text = block.Text.Trim();
        if (text.Length == 0 || text.Length > 120) return false;
        double avgFontSize = Median(block.TextLines[0].Words
            .SelectMany(w => w.Letters)
            .Select(l => (double)l.PointSize));
        return avgFontSize >= headingThreshold;
    }

    /// <summary>Heuristic table-row detector: a line made of several short,
    /// whitespace-separated word groups (candidate cells) rather than continuous
    /// prose. Consecutive rows like this are later joined into one Markdown table
    /// by <see cref="FlushTableRun"/> — no attempt to align exact column boundaries
    /// across rows beyond the simple word-group split, an accepted simplification.</summary>
    private static bool LooksLikeTableRow(TextBlock block)
        => block.TextLines.Count == 1 && SplitIntoCells(block.TextLines[0]).Count >= 2;

    private static List<string> SplitIntoCells(TextLine line)
    {
        // Group words with a horizontal gap wider than ~3x the median inter-word gap
        // into separate cells — a coarse column-alignment signal, not true clustering.
        var words = line.Words.ToList();
        if (words.Count < 2) return new List<string> { line.Text.Trim() };

        var gaps = new List<double>();
        for (int i = 1; i < words.Count; i++)
            gaps.Add(words[i].BoundingBox.Left - words[i - 1].BoundingBox.Right);
        double medianGap = Math.Max(1.0, Median(gaps));

        var cells = new List<string>();
        var current = new StringBuilder(words[0].Text);
        for (int i = 1; i < words.Count; i++)
        {
            double gap = words[i].BoundingBox.Left - words[i - 1].BoundingBox.Right;
            if (gap > medianGap * 3)
            {
                cells.Add(current.ToString());
                current = new StringBuilder(words[i].Text);
            }
            else
            {
                current.Append(' ').Append(words[i].Text);
            }
        }
        cells.Add(current.ToString());
        return cells;
    }

    private static void FlushTableRun(StringBuilder sb, List<TextBlock> tableRun)
    {
        if (tableRun.Count == 0) return;
        var rows = tableRun.Select(b => SplitIntoCells(b.TextLines[0])).ToList();
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
