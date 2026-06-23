using System.Text;
using DocumentFormat.OpenXml.Packaging;
using RagKit;
using UglyToad.PdfPig;

namespace RagKit.Extractors;

/// <summary>Registers PDF and DOCX text extractors so <c>IngestFileAsync</c> handles them.</summary>
public static class DocumentExtractors
{
    /// <summary>Enable PDF (.pdf) and Word (.docx) extraction. Call once at startup.</summary>
    public static void Enable()
    {
        FileExtractors.Register(".pdf", ExtractPdf);
        FileExtractors.Register(".docx", ExtractDocx);
    }

    /// <summary>Extract text from a PDF (page by page).</summary>
    public static string ExtractPdf(string path)
    {
        var sb = new StringBuilder();
        using var doc = PdfDocument.Open(path);
        foreach (var page in doc.GetPages())
            sb.AppendLine(page.Text);
        return sb.ToString().Trim();
    }

    /// <summary>Extract text from a .docx (the document body's text).</summary>
    public static string ExtractDocx(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        return body?.InnerText ?? "";
    }
}
