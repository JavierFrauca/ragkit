# RagKit.Markdown

Normaliza **HTML**, **CSV**, **DOCX** y **PDF** a Markdown antes de ingestar, para
[RagKit](https://www.nuget.org/packages/RagKit). Ingestar siempre sobre un formato
uniforme simplifica el chunking estructural y hace que las citas conserven títulos,
listas y tablas en vez de perderlos (o, en el caso de HTML, arrastrar las etiquetas
literalmente).

```csharp
using RagKit.Markdown;

MarkdownNormalizers.Enable();           // registra los normalizadores
await rag.IngestFileAsync("manual.pdf", domain: "docs");  // ya llega como Markdown
```

Si también usas `RagKit.Extractors` (`DocumentExtractors.Enable()`), llama a
`MarkdownNormalizers.Enable()` **después** — el último `Register` por extensión gana.

La conversión de PDF es heurística (tamaño de fuente para títulos, alineación de
columnas para tablas): buena en documentos de una/dos columnas con tablas simples,
peor en layouts complejos o PDFs escaneados sin capa de texto.

Si prefieres el texto plano extraído por PdfPig sin ninguna clasificación en
títulos/tablas, desactiva el flag antes de ingestar (afecta a `Convert` y
`ConvertAsync`, y por tanto a `.pdf` vía `MarkdownNormalizers.Enable()`):

```csharp
PdfToMarkdown.FormatAsMarkdown = false; // texto plano, sin "## " ni "| | |"
MarkdownNormalizers.Enable();
```

Forma parte de RagKit — RAG agéntico llave en mano para .NET. Documentación completa
en el [repositorio](https://github.com/JavierFrauca/ragkit).
