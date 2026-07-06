using System.Globalization;
using System.Text;
using CsvHelper;

namespace RagKit.Markdown;

/// <summary>CSV → a single Markdown table (header + separator + rows). A CSV file is
/// always one atomic table for the structural chunker (fase 2) — never split by row.</summary>
public static class CsvToMarkdown
{
    public static string Convert(string path)
    {
        using var reader = new StreamReader(path);
        return ConvertCsv(reader);
    }

    public static string ConvertCsv(TextReader reader)
    {
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        if (!csv.Read()) return "";
        csv.ReadHeader();
        var headers = csv.HeaderRecord ?? Array.Empty<string>();
        if (headers.Length == 0) return "";

        var sb = new StringBuilder();
        AppendRow(sb, headers);
        AppendRow(sb, headers.Select(_ => "---"));
        while (csv.Read())
            AppendRow(sb, headers.Select(h => csv.GetField(h) ?? ""));
        return sb.ToString().Trim();
    }

    private static void AppendRow(StringBuilder sb, IEnumerable<string> cells)
        => sb.Append("| ").Append(string.Join(" | ", cells.Select(EscapeCell))).AppendLine(" |");

    private static string EscapeCell(string s) => s.Replace("|", "\\|").Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
}
