# Changelog

Todas las novedades relevantes de RagKit. El formato sigue
[Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/) y el proyecto usa
[SemVer](https://semver.org/lang/es/).

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
