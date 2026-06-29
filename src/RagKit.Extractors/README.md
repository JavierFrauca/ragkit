# RagKit.Extractors

Extractores de texto para [RagKit](https://www.nuget.org/packages/RagKit): **PDF**
(PdfPig) y **DOCX** (OpenXml). El resto de formatos se tratan como texto plano.

```csharp
using RagKit.Extractors;

DocumentExtractors.Enable();            // registra los extractores
await rag.IngestFileAsync("manual.pdf", domain: "docs");  // ya lee PDF/DOCX
```

Forma parte de RagKit — RAG agéntico llave en mano para .NET. Documentación completa
en el [repositorio](https://github.com/JavierFrauca/ragkit).
