# Changelog

Todas las novedades relevantes de RagKit. El formato sigue
[Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/) y el proyecto usa
[SemVer](https://semver.org/lang/es/).

## [0.6.2] - 2026-07-04

### Corregido
- **`QdrantVectorStore.EnumerateAsync` truncaba silenciosamente colecciones
  grandes** — hacía un único scroll con `limit=10000` sin seguir
  `next_page_offset`, así que `ListDocumentsAsync` (que depende del DIM por
  defecto de `IVectorStore` sobre `EnumerateAsync`) subestimaba documentos y
  chunks en colecciones con más de 10000 puntos, sin ningún error. Ahora
  sigue el cursor de scroll nativo de Qdrant hasta agotarlo, igual que ya
  hacía `ListChunksAsync`.
- Documentado en el XML doc de `IVectorStore.AddChunksAsync`/
  `ListDocumentsAsync`/`ListChunksAsync` que sus implementaciones por
  defecto son *fallbacks* correctos pero no necesariamente eficientes ni
  completos a escala — un `IVectorStore` de terceros que no las
  sobrescriba compila sin ningún aviso del compilador.
  ([#27](https://github.com/JavierFrauca/ragkit/issues/27))

## [0.6.1] - 2026-07-04

### Corregido
- **Aislamiento del manifiesto de `IngestIfChangedAsync`** — dos bugs de
  corrección de datos silenciosos encontrados en una revisión de calidad de
  la sesión:
  - `manifestKey` se construía como `$"{domain}:{source}"` sin escapar `:`,
    por lo que dos pares `(domain, source)` distintos (p.ej. `domain="a:b",
    source="c"` y `domain="a", source="b:c"`) podían producir la misma
    clave y pisar el hash de manifiesto del otro documento. Ahora se escapan
    `\` y `:` en cada parte antes de unirlas, con un centinela distinto para
    `domain is null` que nunca colisiona con un valor escapado. Se aplicó el
    mismo escapado en la codificación del catálogo genérico de
    `QdrantVectorStore` por defensa en profundidad.
  - Con `InMemoryVectorStore`, el catálogo (incluido el hash del manifiesto)
    se persiste a disco pero los chunks no — tras reiniciar el proceso con
    el mismo `dataPath`, `IngestIfChangedAsync` confiaba ciegamente en el
    hash superviviente y devolvía `Unchanged` con el store realmente vacío.
    Ahora, antes de aceptar un hash coincidente, se comprueba con
    `ListChunksAsync(source, domain, take: 1)` que el store todavía tiene
    contenido para ese source; si no, se re-ingiere.
  ([#25](https://github.com/JavierFrauca/ragkit/issues/25))

## [0.6.0] - 2026-07-04

### Añadido
- **`RagKit.Dashboard`: CRUD completo (Milestone 2)** — UI funcional (HTML/JS
  embebido, sin build step) + API JSON sobre `RagClient`:
  - Dominios: listar/crear/borrar (con aviso de que borrar un dominio también
    borra en cascada sus documentos, perfiles y guardarails).
  - Etiquetas: listar/crear (RagKit no expone borrado de etiquetas).
  - Documentos: listar por dominio, borrar; visor de chunks paginado con
    cursor (`ListChunksAsync`).
  - Guardarails: listar/crear/borrar (el borrado es por igualdad de valor —
    no tienen id — vía cuerpo JSON en el `DELETE`).
  - Perfiles: listar/crear/borrar.
  - Prompts: leer/editar `OneShotPrompt`/`ChatPrompt`/prompts por dominio en
    caliente (ver `RagClient.OneShotPrompt` etc., Milestone 0).
  - `api/stats` ampliado con recuento de dominios y documentos.
  - Redirección automática cuando se accede a la ruta de montaje sin barra
    final, para que las peticiones relativas del frontend resuelvan bien.
  Verificado además manualmente en navegador (no solo con tests HTTP).
  ([#22](https://github.com/JavierFrauca/ragkit/issues/22))

## [0.5.0] - 2026-07-04

### Añadido
- **`RagKit.Dashboard` (nuevo paquete, esqueleto)**: panel de mantenimiento
  opt-in — Minimal API + assets embebidos como recursos del ensamblado (sin
  build step de frontend), al estilo Qdrant/Hangfire. Este primer milestone
  trae el montaje (`app.MapRagDashboard(path: "/rag-admin")`, devolviendo un
  `IEndpointConventionBuilder` encadenable con `.RequireAuthorization()`) y
  un endpoint `GET api/stats` de humo. El CRUD real (dominios, documentos,
  chunks, guardarails, perfiles, prompts), la ingesta con progreso y el
  playground de preguntas llegan en milestones siguientes.
  Solo target `net10.0` (acoplado a ASP.NET Core, sin el compromiso de
  compatibilidad tan amplio del core). **Sin autenticación propia** — ver
  advertencia de seguridad en `src/RagKit.Dashboard/README.md`.
  ([#22](https://github.com/JavierFrauca/ragkit/issues/22))

## [0.4.0] - 2026-07-04

Primer paso hacia `RagKit.Dashboard` (panel de mantenimiento opt-in, en
desarrollo): expone en `RagClient` la edición en caliente de prompts que
hasta ahora solo era posible reteniendo el `RagOptions` original por fuera.

### Añadido
- `RagClient.OneShotPrompt`/`ChatPrompt` (get/set) y
  `DomainPrompts`/`SetDomainPrompt`/`RemoveDomainPrompt`: forwardean a los
  campos de `RagOptions` que ya se releían en caliente en cada pregunta
  (`SelectPrompt`), así que el cambio surte efecto en la siguiente llamada
  sin recrear el cliente. Antes solo `examples/MiniRag/Rag.cs` conseguía
  esto reteniendo su propio `RagOptions`; ahora es una API soportada para
  cualquier consumidor. ([#20](https://github.com/JavierFrauca/ragkit/issues/20))

## [0.3.2] - 2026-07-04

### Cambiado
- **`RemoveDomainAsync` ahora borra en cascada** los perfiles (`ProfileInfo`)
  y guardarailes (`GuardrailRule`) con `Domain` igual al dominio eliminado
  (cualquier `Profile`). Antes quedaban huérfanos — y peor, se reactivaban
  silenciosamente si más tarde se recreaba un dominio con el mismo nombre,
  ya que `ApplicableRules` solo compara por igualdad de nombre. Los
  guardarailes globales (`Domain == null`) y la configuración de otros
  dominios no se ven afectados. Comportamiento por defecto, sin flag
  opt-in. ([#18](https://github.com/JavierFrauca/ragkit/issues/18))

## [0.3.1] - 2026-07-04

Dos correcciones detectadas en una auditoría de paridad entre los 4 backends.
Issues [#15](https://github.com/JavierFrauca/ragkit/issues/15)/[#16](https://github.com/JavierFrauca/ragkit/issues/16).

### Corregido
- **Perf**: `AddChunksAsync` no hacía batch real en Qdrant/SQL Server/Postgres —
  caían en el bucle por defecto de la interfaz, con una petición HTTP (Qdrant) o
  una conexión/comando separado (SQL Server/Postgres) **por cada chunk**. Ahora
  los 3 escriben el lote completo en una sola llamada (Qdrant: un `PUT` con
  varios puntos; SQL Server/Postgres: un `INSERT` multi-fila sobre una única
  conexión, troceado de 200 en 200 filas para no agotar el límite de
  parámetros). Solo `InMemoryVectorStore` ya lo hacía bien. (#15)
- **Bug**: `SqlServerVectorStore.SearchAsync` filtraba las `labels` requeridas
  sobre-pidiendo `k*5` candidatos por similitud vectorial y descartando en
  proceso los que no las tenían — si más de `k*5` chunks sin la etiqueta
  superaban en similitud al único chunk que sí la tenía, la búsqueda podía
  devolver menos resultados de los que existían, o ninguno. Ahora el filtro
  "contiene todas las etiquetas" se aplica en la propia consulta SQL (vía
  `OPENJSON`, doble `NOT EXISTS`), así que `TOP(@k)` ya ve solo los candidatos
  válidos. Qdrant, Postgres e InMemory ya eran exactos. (#16)

## [0.3.0] - 2026-07-03

Borrado de dominio completo e ids de chunk con listado paginado por documento.
Issues [#12](https://github.com/JavierFrauca/ragkit/issues/12)/[#13](https://github.com/JavierFrauca/ragkit/issues/13).

### Añadido
- **Borrado de dominio completo** (`RagClient.RemoveDomainAsync`, `IVectorStore.DeleteDomainAsync`/`DeleteByDomainAsync`)
  en los 4 backends: borra todos los chunks de un dominio y la propia definición
  del dominio en una sola llamada; limpia también el índice léxico
  (`LexicalIndex.RemoveByDomain`). No toca etiquetas ni perfiles/guardarails
  del dominio. (#12)
- **Id de chunk + listado paginado por documento** (`StoredChunk.Id`/`StoredHit.Id`,
  `IVectorStore.ListChunksAsync`/`RagClient.ListChunksAsync`) en los 4 backends:
  cada chunk expone ahora el id real del backend (antes solo existía en Qdrant y
  nunca se leía de vuelta); `ListChunksAsync(source, domain?, take, cursor?)`
  devuelve una página de chunks con cursor para la siguiente, sin traer la
  colección entera a memoria — scroll nativo en Qdrant, keyset pagination por
  `id` en SQL Server/Postgres, offset en memoria. (#13)

### Cambiado
- `StoredHit`/`StoredChunk` incorporan `Id` como último parámetro con valor por
  defecto (`""`) — no rompe construcciones posicionales existentes.

## [0.2.1] - 2026-07-03

### Corregido
- **Bug**: el manifiesto de idempotencia de `IngestIfChangedAsync` (y por herencia
  `IngestFileIfChangedAsync`/`IngestFolderAsync`) se guardaba con clave = solo
  `source`, sin `domain`. En una base con varios dominios compartiendo colección,
  ingestar el mismo nombre de fichero en un segundo dominio borraba silenciosamente
  los chunks del primero (`RemoveDocumentAsync` se llamaba con `domain: null`).
  Ahora la clave del manifiesto es `{domain}:{source}` cuando se pasa un dominio
  explícito, y el borrado por cambio de contenido se acota a ese mismo dominio.
  ([#10](https://github.com/JavierFrauca/ragkit/issues/10))

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
