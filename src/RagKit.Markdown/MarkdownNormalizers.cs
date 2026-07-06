namespace RagKit.Markdown;

/// <summary>
/// Registers Markdown-producing extractors for HTML, CSV, DOCX and PDF, so
/// <c>RagClient.IngestFileAsync</c> always receives Markdown regardless of the
/// source format. Overwrites any previous registration for the same extension
/// (e.g. <c>RagKit.Extractors.DocumentExtractors.Enable()</c>'s plain-text PDF/DOCX
/// extractors) — call this one LAST if both are enabled.
/// </summary>
public static class MarkdownNormalizers
{
    public static void Enable()
    {
        FileExtractors.Register(".html", HtmlToMarkdown.Convert);
        FileExtractors.Register(".htm", HtmlToMarkdown.Convert);
        FileExtractors.Register(".csv", CsvToMarkdown.Convert);
        FileExtractors.Register(".docx", DocxToMarkdown.Convert);
        FileExtractors.Register(".pdf", PdfToMarkdown.Convert);
    }
}
