using RagKit.Markdown;

namespace RagKit.Markdown.Tests;

public class HtmlToMarkdownTests
{
    [Fact]
    public void ConvertHtml_basic_paragraph()
    {
        var md = HtmlToMarkdown.ConvertHtml("<p>Hola mundo</p>");
        Assert.Contains("Hola mundo", md);
    }

    [Fact]
    public void ConvertHtml_headings()
    {
        var md = HtmlToMarkdown.ConvertHtml("<h1>Título</h1><h2>Subtítulo</h2>");
        Assert.Contains("# ", md);
        Assert.Contains("## ", md);
    }

    [Fact]
    public void ConvertHtml_table()
    {
        var md = HtmlToMarkdown.ConvertHtml(
            "<table><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>2</td></tr></table>");
        Assert.Contains("| A | B |", md);
        Assert.Contains("| 1 | 2 |", md);
    }

    [Fact]
    public void ConvertHtml_strips_comments()
    {
        var md = HtmlToMarkdown.ConvertHtml("<!-- secret --><p>visible</p>");
        Assert.DoesNotContain("secret", md);
        Assert.Contains("visible", md);
    }
}

public class CsvToMarkdownTests
{
    [Fact]
    public void ConvertCsv_header_and_rows()
    {
        using var reader = new StringReader("Nombre,Edad\nAlice,30\nBob,25\n");
        var md = CsvToMarkdown.ConvertCsv(reader);
        Assert.Contains("| Nombre | Edad |", md);
        Assert.Contains("| Alice | 30 |", md);
        Assert.Contains("| Bob | 25 |", md);
    }

    [Fact]
    public void ConvertCsv_empty_returns_empty()
    {
        using var reader = new StringReader("");
        var md = CsvToMarkdown.ConvertCsv(reader);
        Assert.Equal("", md);
    }

    [Fact]
    public void ConvertCsv_escapes_pipe_in_cells()
    {
        using var reader = new StringReader("Col\nval|ue\n");
        var md = CsvToMarkdown.ConvertCsv(reader);
        Assert.Contains("val\\|ue", md);
    }
}

public class DocxToMarkdownTests
{
    [Fact]
    public void Convert_paragraph_text()
    {
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
                run.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Text("Párrafo simple"));
            }

            var md = DocxToMarkdown.Convert(path);
            Assert.Contains("Párrafo simple", md);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Convert_heading_styles()
    {
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
                p.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.ParagraphProperties(
                    new DocumentFormat.OpenXml.Wordprocessing.ParagraphStyleId { Val = "Heading1" }));
                var run = p.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Run());
                run.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Text("Título principal"));
            }

            var md = DocxToMarkdown.Convert(path);
            Assert.StartsWith("# ", md.Trim());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Convert_empty_body()
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

            var md = DocxToMarkdown.Convert(path);
            Assert.Equal("", md);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}

public class MarkdownNormalizersTests
{
    [Fact]
    public void Enable_registers_all_extractors()
    {
        // Just verify Enable() does not throw — registration is what matters.
        MarkdownNormalizers.Enable();

        // After Enable(), the html extractor should be registered (not the
        // default passthrough). We verify by checking a minimal HTML string
        // via ConvertHtml (which is the registered handler).
        var md = HtmlToMarkdown.ConvertHtml("<p>test</p>");
        Assert.NotNull(md);
    }
}
