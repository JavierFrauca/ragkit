using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace RagKit.Markdown;

/// <summary>DOCX → Markdown by walking the OOXML body directly (no new dependency —
/// DocumentFormat.OpenXml is already a RagKit.Extractors dependency): paragraph
/// heading styles → "#".."######", tables → Markdown tables, numbered/bulleted
/// paragraphs → "- " list items (list level/type nuance from numbering.xml is not
/// resolved — every list renders as a flat bullet list, an accepted 80% simplification).</summary>
public static class DocxToMarkdown
{
    public static string Convert(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return "";

        var sb = new StringBuilder();
        foreach (var element in body.Elements())
            AppendElement(sb, element);
        return sb.ToString().Trim();
    }

    private static void AppendElement(StringBuilder sb, OpenXmlElement element)
    {
        switch (element)
        {
            case Paragraph p: AppendParagraph(sb, p); break;
            case Table t: AppendTable(sb, t); break;
        }
    }

    private static void AppendParagraph(StringBuilder sb, Paragraph p)
    {
        var text = GetText(p);
        if (string.IsNullOrWhiteSpace(text)) { sb.AppendLine(); return; }

        int headingLevel = GetHeadingLevel(p);
        if (headingLevel > 0)
        {
            sb.Append(new string('#', headingLevel)).Append(' ').AppendLine(text.Trim());
            sb.AppendLine();
            return;
        }

        bool isListItem = p.ParagraphProperties?.NumberingProperties is not null;
        if (isListItem)
            sb.Append("- ").AppendLine(text.Trim());
        else
            sb.AppendLine(text.Trim());
        sb.AppendLine();
    }

    /// <summary>Maps the paragraph's style id ("Heading1".."Heading6", also matching the
    /// localized "Ttulo"/"Titre" prefixes some Word locales use) to a Markdown heading
    /// level (1-6), or 0 when the paragraph isn't a heading.</summary>
    private static int GetHeadingLevel(Paragraph p)
    {
        var styleId = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (string.IsNullOrEmpty(styleId)) return 0;

        foreach (var prefix in HeadingStylePrefixes)
        {
            if (styleId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(styleId.AsSpan(prefix.Length), out int level))
                return Math.Clamp(level, 1, 6);
        }
        return 0;
    }

    private static readonly string[] HeadingStylePrefixes = { "Heading", "Ttulo", "Titre" };

    private static string GetText(OpenXmlElement element)
    {
        var sb = new StringBuilder();
        foreach (var node in element.Descendants())
        {
            switch (node)
            {
                case Text t: sb.Append(t.Text); break;
                case TabChar: sb.Append('\t'); break;
                case Break: sb.Append(' '); break;
            }
        }
        return sb.ToString();
    }

    private static void AppendTable(StringBuilder sb, Table table)
    {
        var rows = table.Elements<TableRow>().ToList();
        if (rows.Count == 0) return;

        for (int r = 0; r < rows.Count; r++)
        {
            var cells = rows[r].Elements<TableCell>().Select(c => GetText(c).Trim().Replace("|", "\\|").Replace('\n', ' '));
            sb.Append("| ").Append(string.Join(" | ", cells)).AppendLine(" |");
            if (r == 0)
            {
                int colCount = rows[0].Elements<TableCell>().Count();
                sb.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", colCount))).AppendLine(" |");
            }
        }
        sb.AppendLine();
    }
}
