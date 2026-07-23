# Plan de Mejoras RagKit v2.0

14 mejoras priorizadas por impacto × esfuerzo.

---

## 🔴 Alta prioridad

### 1. Observabilidad — logging + tracing

**Problema:** No hay `ILogger` ni `ActivitySource` en ningún sitio. Cada
llamada LLM, cada retrieval, cada ingesta es una caja negra en producción.
El README lo reconoce como "conocido post-1.0".

**Solución:**
- Añadir `Microsoft.Extensions.Logging.Abstractions`
- `RagOptions.LoggerFactory` (`ILoggerFactory?`, `null` → `NullLoggerFactory`)
- `ActivitySource` "RagKit" con estos spans:
  - `RagKit.Query` → `.Route` / `.GuardInput` / `.Retrieve` / `.Answer` / `.GuardOutput`
  - `RagKit.Ingest` → `.Extract` / `.Classify` / `.Chunk` / `.Embed` / `.Store`
  - `RagKit.Agent.Step`
- Tags: `ragkit.domain`, `ragkit.model`, `ragkit.store_kind`, `ragkit.duration_ms`
- `ILogger` en `OpenAiChatClient`, `OnnxEmbedder`, `MarkdownChunker`, `LexicalIndex`, `Guardrail`

**Archivos:** `src/RagKit/Internal/Telemetry.cs` (nuevo), `RagOptions.cs`, varios internos

**Esfuerzo:** 5 días · **Tests:** 6

---

### 2. Excepciones tipadas

**Problema:** Todo lanza `RagKitException` genérica. El consumidor no puede
diferenciar un timeout del LLM (reintentable) de un mismatch de embedding
(abortar).

**Solución:**
- `LlmException` — HTTP 4xx/5xx o timeout del LLM
- `StoreException` — error en Qdrant/Postgres/SQL Server
- `ClassificationException` — tier-2 devuelve JSON no parseable
- `ChunkingException` — Markdig lanza error
- `EmbeddingMismatchException` — ya existe, sin cambios
- Sustituir `throw new RagKitException(...)` por la concreta en todo el código

**Archivos:** `src/RagKit/Exceptions.cs` (nuevo), `OpenAiChatClient.cs`,
`MarkdownChunker.cs`, stores, `Guardrail.cs`, `Classifier.cs`

**Esfuerzo:** 2 días · **Tests:** 5

---

### 3. Rate limiting interno

**Problema:** Si el consumidor lanza varias ingestiones en paralelo o
múltiples `AskAsync` concurrentes, no hay nada que limite las llamadas al
LLM. El tier-2 (normalmente barato/rápido) se puede saturar.

**Solución:**
- `RagOptions.MaxConcurrentLlmCalls` (int, default `5`) — `SemaphoreSlim`
  compartido en `OpenAiChatClient` para todas las llamadas (clasificación,
  enrutado, guardrail, summarizer, tier-1)
- `RagOptions.MaxConcurrentIngestions` (int, default `2`) — `SemaphoreSlim`
  en `IngestAsync`
- `ConcurrencyGate` interno con los dos semáforos

**Archivos:** `src/RagKit/Internal/ConcurrencyGate.cs` (nuevo),
`RagOptions.cs`, `OpenAiChatClient.cs`, `RagClient.cs`

**Esfuerzo:** 3 días · **Tests:** 4

---

### 4. CI con servicios reales (docker-compose)

**Problema:** Los tests de integración contra Qdrant, Postgres y SQL Server
se auto-omiten en CI. Los bugs entre backends no se detectan hasta que un
usuario los reporta. El changelog de 1.0.0 lo admite explícitamente.

**Solución:**
- `docker-compose.yml` con Qdrant, Postgres+pgvector, SQL Server 2025
- Job `integration-tests` en el workflow de CI que levanta servicios,
  ejecuta `dotnet test` y derriba
- Health checks con reintentos para SQL Server (arranque lento)
- Variables de entorno: `RAGKIT_QDRANT_URL`, `RAGKIT_POSTGRES_CONNECTION`,
  `RAGKIT_SQLSERVER_CONNECTION`

**Archivos:** `docker-compose.yml` (nuevo), `.github/workflows/ci.yml`

**Esfuerzo:** 3 días · **Tests:** 0 (los tests ya existen, solo se activan)

---

### 5. Tests para conectores sin cobertura

**Problema:** 5 de los 8 paquetes no tienen proyecto de tests propio:
`RagKit.Onnx`, `RagKit.Mcp`, `RagKit.Extractors`, `RagKit.Markdown`,
`RagKit.Postgres`, `RagKit.SqlServer`. Solo hay tests en `RagKit.Tests` que
prueban algunos adaptadores con fakes. `RagKit.Onnx` — la pieza más crítica
(embeddings incorrectos = datos corruptos) — no tiene cobertura real.

**Solución — 6 proyectos de test nuevos:**

| Proyecto | Tests | Foco |
|---|---|---|
| `RagKit.Onnx.Tests` | 12 | Fairseq remap, pooling Mean/Cls, 3 modelos reales, batch |
| `RagKit.Mcp.Tests` | 5 | JSON-RPC inválido, reconexión stdio |
| `RagKit.Extractors.Tests` | 4 | PDF/DOCX/TXT reales, registro de extractores |
| `RagKit.Markdown.Tests` | 8 | PDF con tabla, DOCX con headings, HTML, CSV |
| `RagKit.Postgres.Tests` | 6 | SearchAsync, ListChunksAsync con cursor, catálogo |
| `RagKit.SqlServer.Tests` | 6 | Misma batería que Postgres |

**Esfuerzo:** 10 días · **Tests:** 41

---

## 🟡 Media prioridad

### 6. Cache de embeddings para `find_domains` / `find_labels`

**Problema:** Documentado en el README como mejora natural pendiente: cada
llamada recalcula el embedding de cada descripción. Con 100+ dominios esto
añade latencia proporcional e innecesaria.

**Solución:**
- `ConcurrentDictionary<string, (float[] Vector, int Version)>` en `InternalTools`
- Versión incrementada en `DefineDomainAsync`/`DefineLabelAsync`/`Remove*Async`
- `RagOptions.EnableEmbeddingCache` (default `true`)
- Invalidación granular, no global

**Archivos:** `src/RagKit/Agent/InternalTools.cs`

**Esfuerzo:** 1 día · **Tests:** 2

---

### 7. Stemming en BM25

**Problema:** El tokenizador del índice léxico divide por no-alfanuméricos
y descarta tokens < 2 caracteres. Sin stemming, "contrato" y "contratos"
son términos distintos. El recall léxico pierde plurales y conjugaciones.

**Solución:**
- Algoritmo Snowball para español e inglés (~100 líneas cada uno, cero
  dependencias externas)
- `RagOptions.Bm25Stemming` (default `true`)
- Aplicar stemming al tokenizar, tanto en indexación como en búsqueda
- Stop words en español e inglés

**Archivos:** `src/RagKit/Internal/LexicalIndex.cs`,
`src/RagKit/Internal/Stemmer.cs` (nuevo)

**Esfuerzo:** 2 días · **Tests:** 4

---

### 8. Cancelación en chunking

**Problema:** `Chunker.Chunk` y `MarkdownChunker.Chunk` son síncronos y no
aceptan `CancellationToken`. Para documentos grandes (BOE consolidado, >1M
caracteres) el chunking puede tardar segundos sin posibilidad de cancelación
— el `CancellationToken` que `IngestAsync` ya recibe nunca llega aquí.

**Solución:**
- `Chunker.ChunkAsync(text, size, overlap, ct)` — comprueba `ct` entre chunks
- `MarkdownChunker.ChunkAsync(markdown, maxChars, overlap, ct)` — entre secciones
- Pasar `ct` desde `IngestAsync` hasta el chunker

**Archivos:** `src/RagKit/Internal/MarkdownChunker.cs`,
`src/RagKit/Internal/Components.cs`

**Esfuerzo:** 2 días · **Tests:** 3

---

### 9. Progreso granular en ingesta

**Problema:** `IngestFolderAsync` reporta un resultado por fichero, pero un
solo documento de 600K caracteres con 200+ chunks no emite nada hasta
terminar. El consumidor no sabe si va por el chunk 5 o el 180.

**Solución:**
- `IngestProgress` record: `ChunksEmbedded`, `TotalChunks`, `CurrentStage`
- Parámetro `IProgress<IngestProgress>?` en `IngestAsync`, `IngestFileAsync`,
  `IngestFolderAsync`
- Reportar tras clasificar, tras chunkear, y cada N chunks embedidos
- `IngestFolderAsync` reporta progreso dentro de cada fichero, no solo entre
  ficheros

**Archivos:** `src/RagKit/Abstractions.cs`, `src/RagKit/RagClient.cs`

**Esfuerzo:** 3 días · **Tests:** 3

---

### 10. Refactor de `RagClient` en handlers internos

**Problema:** `RagClient.cs` tiene ~1000 líneas y mezcla ingesta, query,
streaming, bucle agéntico, CRUD de perfiles/guardrails, MCP y catálogo.
Cualquier cambio toca un archivo enorme.

**Solución — extraer 3 clases internas:**

| Handler | Contiene |
|---|---|
| `IngestionHandler` | `IngestAsync`, `IngestFileAsync`, `IngestIfChangedAsync`, `IngestFolderAsync`, `SafeSummarizeAsync`, `BuildEmbedText`, manifiesto |
| `QueryHandler` | `AskAsync` (3), `AskStreamAsync` (2), `ResolveRouteAsync`, guardrails, `RetrieveAsync`, `SelectPrompt`, `RouteQueryAsync`, `StartChat` |
| `AgentHandler` | `AskAgentCoreAsync`, `AskAgentStreamCoreAsync`, `BuildAgentTools`, `RunAgentLoopAsync` |

`RagClient` queda como fachada (~200 líneas) que forwardea a los handlers y
expone el CRUD de estructura/guardrails/perfiles.

**Archivos:** 3 nuevos en `src/RagKit/Internal/`

**Esfuerzo:** 4 días · **Tests:** 0 (los 294 existentes deben pasar sin cambios)

---

## 🟢 Baja prioridad

### 11. Prompts de sistema en inglés

**Problema:** `Prompt.DefaultSystem`, `Prompt.AgentSystem`,
`Prompt.GuardrailInputSystem` están hardcodeados en español. Cualquier
consumidor no hispanohablante tiene que sobreescribirlos.

**Solución:**
- Constantes en inglés: `DefaultSystemEn`, `AgentSystemEn`, etc.
- `RagOptions.SystemPromptLanguage` (default `"es"` para no romper a nadie)
- `Prompt.DefaultSystem` → propiedad que resuelve según idioma

**Archivos:** `src/RagKit/Internal/Components.cs`, `RagOptions.cs`

**Esfuerzo:** 1 día · **Tests:** 2

---

### 12. Metadata arbitraria por documento

**Problema:** No hay forma de adjuntar metadatos como `Author`, `Department`,
`Confidentiality`, `ExpiresAt` a un chunk. Solo `Source` y `Labels`.

**Solución:**
- `IReadOnlyDictionary<string, string>? Metadata` en `EmbeddedChunk`,
  `StoredChunk`, `StoredHit` (con valor por defecto `null`)
- Parámetro `metadata` en `IngestAsync`
- Persistido como JSON en los 4 backends
- `RagOptions.MetadataFilter` para filtrar en retrieval

**Archivos:** `src/RagKit/Stores/IVectorStore.cs`, los 4 stores,
`RagClient.cs`, `RagOptions.cs`

**Esfuerzo:** 3 días · **Tests:** 5

---

### 13. Auth por API Key en el Dashboard

**Problema:** `MapRagDashboard` no tiene auth propia. El README advierte
"no lo expongas público sin ello". El consumidor tiene que montar su propio
esquema con `RequireAuthorization`.

**Solución:**
- `RagDashboardOptions` con `AuthenticationScheme` (`None` | `ApiKey`),
  `ApiKey`, `ApiKeyHeaderName` (default `X-Api-Key`)
- Middleware interno que valida la cabecera
- `MapRagDashboard` acepta `RagDashboardOptions?`

**Archivos:** `src/RagKit.Dashboard/RagDashboardOptions.cs` (nuevo),
`RagDashboardExtensions.cs`

**Esfuerzo:** 2 días · **Tests:** 3

---

### 14. Índice BM25 con respaldo en disco para gran escala

**Problema:** `LexicalIndex` mantiene todo el texto de cada chunk + su mapa
de TF en memoria. Para >100K chunks esto puede ocupar cientos de MB.

**Solución:**
- Extraer `ILexicalIndex` de `LexicalIndex`
- `InMemoryLexicalIndex` (el actual)
- `SqliteLexicalIndex` con `Microsoft.Data.Sqlite` (ya incluido en .NET)
- `RagOptions.LexicalIndexMaxChunks` (default `100_000`): al superarlo,
  `RagClient` migra automáticamente al backend SQLite

**Archivos:** `src/RagKit/Internal/ILexicalIndex.cs` (nuevo),
`LexicalIndex.cs`, `SqliteLexicalIndex.cs` (nuevo)

**Esfuerzo:** 6 días · **Tests:** 8

---

## Orden de ejecución

```
Semana 1-2   │  1. Observabilidad
             │  2. Excepciones tipadas
             │
Semana 3     │  3. Rate limiting
             │
Semana 4-5   │  4. CI con docker-compose     (paralelizable con semana 3)
             │  5. Tests conectores (inicio)
             │
Semana 6-7   │  5. Tests conectores (fin)
             │
Semana 8     │  6. Cache embeddings
             │  7. Stemming BM25
             │
Semana 9     │  8. Cancelación chunking
             │  9. Progreso granular
             │
Semana 10    │ 10. Refactor RagClient
             │ 11. Prompts en inglés
             │
Semana 11-12 │ 12. Metadata arbitraria
             │ 13. Auth Dashboard
             │ 14. BM25 en disco
```

## Dependencias

```
1. Observabilidad ─────────────────────────────────────────────────────┐
2. Excepciones ──────────────────── (independiente, paralelizable) ────┤
                                                                        │
3. Rate limiting ───────────────── (independiente) ────────────────────┤
4. CI docker-compose ───────────── (independiente) ────────────────────┤
5. Tests conectores ────────────── depende de 4 ───────────────────────┤
                                                                        │
6. Cache embeddings ────────────── (independiente) ────────────────────┤
7. Stemming BM25 ───────────────── (independiente) ────────────────────┤
8. Cancelación chunking ────────── (independiente) ────────────────────┤
9. Progreso granular ───────────── (independiente) ────────────────────┤
10. Refactor RagClient ─────────── (independiente) ────────────────────┤
11. Prompts en inglés ──────────── (independiente) ────────────────────┤
12. Metadata ───────────────────── (independiente) ────────────────────┤
13. Auth Dashboard ─────────────── (independiente) ────────────────────┤
14. BM25 en disco ──────────────── depende de 7 ───────────────────────┘
```

La mayoría de tareas son independientes entre sí. Solo 5 → 4, 14 → 7 tienen
dependencia real. El orden de arriba es una sugerencia; se pueden reordenar
según prioridad del usuario.

## Total

| Métrica | Valor |
|---|---|
| Mejoras | 14 |
| Tests nuevos | ~90 |
| Semanas | 12 |
| Archivos nuevos | ~20 |
| Breaking changes | 0 |
