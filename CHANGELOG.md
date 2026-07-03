# Changelog

Todas las novedades relevantes de RagKit. El formato sigue
[Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/) y el proyecto usa
[SemVer](https://semver.org/lang/es/).

## [0.2.0] - 2026-07-03

Gestión de documentos (borrado, inventario, idempotencia, ingesta de carpeta,
catálogo genérico) y ask multi-turno con historial explícito. Backlog completo
en los issues [#2](https://github.com/JavierFrauca/ragkit/issues/2)–[#8](https://github.com/JavierFrauca/ragkit/issues/8).

### Añadido
- **Borrado de documentos por `source`** (`IVectorStore.DeleteBySourceAsync`,
  `RagClient.RemoveDocumentAsync`) en los 4 backends; también limpia el índice
  léxico en memoria cuando la búsqueda híbrida está activa. (#2)
- **Inventario de documentos** (`ListDocumentsAsync`, `DocumentInfo`): agrega los
  chunks internos por `source`, con recuento y fecha de última ingesta. (#3)
- **Ingesta idempotente por hash de contenido** (`IngestIfChangedAsync`,
  `IngestFileIfChangedAsync`): hashea el texto, lo compara contra un manifiesto
  persistido en el catálogo del store y evita reclasificar/reembeber contenido sin
  cambios; si cambió, sustituye atómicamente los chunks anteriores. (#4)
- **Ingesta de carpeta completa** (`IngestFolderAsync`), como
  `IAsyncEnumerable<IngestResult>` para reportar progreso incremental; filtra por
  extensión soportada (`FileExtractors.IsSupported`) y no aborta el resto de la
  carpeta si un fichero falla. (#5)
- **Catálogo genérico key-value** (`Get/Save/DeleteCatalogEntryAsync`) expuesto al
  consumidor en los 4 backends, para metadatos propios de la aplicación (usado
  internamente por el manifiesto de `IngestIfChangedAsync`). (#6)
- **`IngestOutcome`** de tres estados (`Ingested | Rejected | Unchanged`) en
  `IngestResult`, sustituyendo al booleano `Rejected` (que se conserva como
  propiedad de solo lectura derivada, sin romper a los consumidores existentes). (#7)
- **`AskAsync`/`AskStreamAsync` con historial explícito** (`priorHistory` como
  parámetro): alternativa a `ChatSession` como función pura `(historial, pregunta)
  → respuesta`, sin objeto de sesión mutable compartido — el consumidor persiste y
  reconstruye el historial en cada turno. (#8)

### Cambiado
- `IVectorStore.AddChunkAsync` y `EmbeddedChunk`/`StoredChunk` incorporan
  `IngestedAtUtc` (con valor por defecto, no rompe implementaciones existentes que
  ya pasaban solo los parámetros previos).

## [0.1.0] - 2026-06-28

Primera versión publicada en NuGet.

### Añadido
- Fachada `RagClient` con dos tiers (LLM de respuesta + clasificador), dominios y
  etiquetas, prompts configurables (one-shot / chat) y respuestas con citas.
- **Enrutado en query-time**: el tier-2 elige dominio y **perfiles** (lentes) por
  pregunta, con degradación elegante y dominio opcional en mono-dominio.
- **Perfiles** por dominio con prompt enfocado y, opcionalmente, etiquetas que acotan
  la recuperación (multi-perfil configurable).
- **Guardarails** de entrada (siempre activo: checks deterministas + red de seguridad
  LLM + reglas) y de salida (también en streaming, mediante buffer). El enrutado y los
  guardarails se aplican también en el **modo agéntico**.
- **CRUD + persistencia** de perfiles y guardarails (`DefineProfileAsync`,
  `RemoveProfileAsync`, `DefineGuardrailAsync`, …) en los 4 backends, con `RagOptions`
  como semilla inicial; tools de agente `create_profile`/`create_guardrail`.
- Recuperación **híbrida** (vector denso + BM25 con RRF) y reranker opcional.
- `IVectorStore` con factory por enum: **InMemory**, **Qdrant**, **Postgres + pgvector**
  (`RagKit.Postgres`) y **SQL Server 2025** tipo `VECTOR` (`RagKit.SqlServer`).
- Embeddings: local (hash), **ONNX** (`RagKit.Onnx`, tokenizer WordPiece **o
  SentencePiece** para modelos multilingües) y API compatible OpenAI/Ollama.
- Reranker **cross-encoder** ONNX de fábrica (`OnnxCrossEncoderReranker`).
- Guardarail determinista de **PII** opcional (`GuardrailPiiCheck`).
- Bucle de agente con herramientas internas + **MCP** externo (`RagKit.Mcp`),
  conectable por código (`AddStdioServerAsync`) o por configuración (`RagOptions.Mcps`
  + `McpServers.Enable()`).
- Extractores **PDF/DOCX** (`RagKit.Extractors`) y **streaming** de respuestas (SSE).

### Empaquetado
- Multi-target **net10.0** y **net8.0**.
- Metadatos unificados, README embebido por paquete, icono, Source Link y paquetes
  de símbolos (`.snupkg`).
