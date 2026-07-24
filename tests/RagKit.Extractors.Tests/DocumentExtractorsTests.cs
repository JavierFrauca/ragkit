using RagKit.Extractors;

namespace RagKit.Extractors.Tests;

public class DocumentExtractorsTests
{
    [Fact]
    public void Enable_registers_pdf_and_docx_extractors()
    {
        // Reset to a clean state before the test: unregister any prior handler.
        FileExtractors.Register(".pdf", (string _) => "");
        FileExtractors.Register(".docx", (string _) => "");

        DocumentExtractors.Enable();

        // After Enable(), the handlers should produce real output (not empty)
        // — we can verify they're no longer the dummy handler by checking
        // they don't throw on a nonexistent file (PdfPig/OpenXml throw).
        Assert.ThrowsAny<Exception>(() => FileExtractors.Extract("nonexistent.pdf"));
        Assert.ThrowsAny<Exception>(() => FileExtractors.Extract("nonexistent.docx"));
    }

    [Fact]
    public void ExtractTxt_passes_through_verbatim()
    {
        // .txt has no registered extractor by default, so FileExtractors
        // returns the file content as-is (the default fallback reads text).
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "contenido de prueba");
            // When no custom extractor is registered for .txt, the built-in
            // fallback reads the file as plain text.
            var result = FileExtractors.Extract(path);
            Assert.Contains("contenido", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExtractDocx_reads_paragraph_text()
    {
        // Generate a minimal .docx in-memory using OpenXml, save to temp,
        // extract, and verify. This avoids needing binary fixture files.
        var path = Path.GetTempFileName() + ".docx";
        try
        {
            using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(path,
                DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var mainPart = doc.AddMainDocumentPart();
                mainPart.Document = new DocumentFormat.OpenXml.Wordprocessing.Document();
                var body = mainPart.Document.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Body());
                var p = body.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Paragraph());
                var run = p.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Run());
                run.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Text("Hola mundo desde DOCX"));
            }

            var text = DocumentExtractors.ExtractDocx(path);
            Assert.Contains("Hola mundo desde DOCX", text);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ExtractDocx_handles_empty_document()
    {
        var path = Path.GetTempFileName() + ".docx";
        try
        {
            using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(path,
                DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                doc.AddMainDocumentPart().Document =
                    new DocumentFormat.OpenXml.Wordprocessing.Document(
                        new DocumentFormat.OpenXml.Wordprocessing.Body());
            }

            var text = DocumentExtractors.ExtractDocx(path);
            Assert.Equal("", text);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
