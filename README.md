# RagKit

**RAG agéntico llave en mano.** Para el desarrollador que tiene que "montar un
RAG" y no quiere pelearse con embeddings, bases vectoriales ni pipelines. Añade
el paquete, configura **dos LLM**, define tus **dominios y etiquetas**, suelta
documentos (se clasifican solos) y pregunta.

```csharp
using RagKit;

// Crea e inicializa el cliente (resuelve embedder y store por ti).
var rag = await RagClient.CreateAsync(new RagOptions
{
    // Tier-1: el modelo bueno que redacta las respuestas
    Answer     = new LlmConfig { Url = "https://api.deepseek.com", ApiKey = "sk-…", Model = "deepseek-chat" },
    // Tier-2: uno más barato/rápido que clasifica documentos y enruta preguntas
    Classifier = new LlmConfig { Url = "https://api.deepseek.com", ApiKey = "sk-…", Model = "deepseek-chat" },
});

// Defines tu estructura (con descripciones que guían al tier-2)
await rag.DefineDomainAsync("fiscal", "impuestos, IVA, IRPF, tributos");
await rag.DefineDomainAsync("rrhh", "personal, nóminas, contratos");
await rag.DefineLabelAsync("iva");
await rag.DefineLabelAsync("irpf");
await rag.DefineLabelAsync("contrato");

// Ingestas: el tier-2 decide dominio y etiquetas (o los pasas tú a mano)
var r = await rag.IngestAsync("El tipo general del IVA es del 21%…", source: "iva.txt");
// r.Domain == "fiscal", r.Labels == ["iva"]

// Preguntas: el tier-1 responde, acotado al dominio y con citas
var a = await rag.AskAsync("¿Cuál es el IVA general?", domain: "fiscal");
Console.WriteLine(a.Answer);
foreach (var c in a.Citations) Console.WriteLine($"  · [{c.Source}] {c.Snippet}");
```

No configuras modelo de embeddings ni base de datos: **se resuelve por ti**.

## Instalación
```bash
dotnet add package RagKit                 # core
# conectores opcionales (cada uno se activa con su Enable()):
dotnet add package RagKit.Extractors      # PDF/DOCX
dotnet add package RagKit.Onnx            # embeddings ONNX locales
dotnet add package RagKit.Postgres        # Postgres + pgvector
dotnet add package RagKit.SqlServer       # SQL Server 2025 (VECTOR)
dotnet add package RagKit.Mcp            # herramientas MCP externas
dotnet add package RagKit.Dashboard      # panel de mantenimiento opt-in (net10.0)
```
Compatible con **.NET 8** y **.NET 10** (`net8.0` / `net10.0`) — salvo
`RagKit.Dashboard`, acoplado a ASP.NET Core, que solo target `net10.0`.

## Lo que configuras
| Opción | Para qué |
|---|---|
| `Answer` | LLM **tier-1** (compatible OpenAI) que redacta respuestas. |
| `Classifier` | LLM **tier-2** que clasifica documentos y enruta preguntas. Si lo omites, se reusa `Answer`. |
| `OneShotPrompt` / `ChatPrompt` *(opc.)* | Prompt de sistema (Markdown) para one-shot y para chat. Por defecto, uno con citas. |
| `TopK` *(opc.)* | Fragmentos de contexto (5 por defecto). |
| `AutoClassify` *(opc.)* | Si el tier-2 clasifica solo en la ingesta (on por defecto). |
| `Profiles` *(opc.)* | "Lentes" por dominio: cada una con su prompt enfocado y, opcionalmente, etiquetas que acotan la búsqueda. El tier-2 las selecciona en query-time. |
| `Guardrails` *(opc.)* | Reglas (entrada/salida) en lenguaje natural + checks deterministas, con defaults seguros. |
| `EnableQueryRouting` *(opc.)* | El tier-2 enruta la pregunta a dominio/perfil si no los pasas a mano (on por defecto). |

Cualquier endpoint **compatible OpenAI** vale: OpenAI, DeepSeek, Groq, Mistral, o
local con Ollama/vLLM/LM Studio (solo cambia `Url`+`ApiKey`).

## Formas de usarlo
```csharp
var a = await rag.AskAsync("…");                           // 1) RAG one-shot; enruta dominio/perfil solo
var a2 = await rag.AskAsync("…", domain: "fiscal", profile: "asesor"); //    …o los fijas tú
var chat = rag.StartChat(domain: "fiscal");                // 2) Chat con memoria (enruta en el 1er turno)
var g = await rag.AskAgentAsync("…", domain: "fiscal");    // 3) Agéntico: el modelo decide
//        buscar / crear dominios·etiquetas / ingestar / llamar MCP (requiere modelo con tools;
//        si no, cae a one-shot automáticamente)

// 4) Streaming (token a token). Las citas están listas antes de generar.
var s = await rag.AskStreamAsync("…", domain: "fiscal");
await foreach (var token in s.Tokens) Console.Write(token);   // s.Citations ya disponible
// chat.AskStreamAsync(...) hace lo mismo en una sesión con memoria.

// 4b) Agéntico streameado: no solo la respuesta final token a token, también
//     eventos de qué herramienta se está usando y cuándo termina.
var ags = await rag.AskAgentStreamAsync("…", domain: "fiscal", tools: AgentToolScope.SearchOnly);
await foreach (var e in ags.Events)
{
    if (e.Kind == AgentStreamEventKind.ToolCallStarted) Console.WriteLine($"🔍 usando {e.ToolName}…");
    if (e.Kind == AgentStreamEventKind.Token) Console.Write(e.Token);
}

// 5) Multi-turno sin estado interno oculto: el historial es un parámetro explícito
//    (función pura, sin objeto de sesión que sobreviva en memoria del proceso — lo
//    reconstruyes tú desde tu propio almacén en cada turno).
IReadOnlyList<ChatMessage> historial = Array.Empty<ChatMessage>();
var t1 = await rag.AskAsync("¿qué sección de cable para 25 A?", historial, domain: "construccion");
historial = new[] { new ChatMessage("user", "¿qué sección de cable para 25 A?"), new ChatMessage("assistant", t1.Answer) };
var t2 = await rag.AskAsync("¿y para 40 A?", historial, domain: "construccion");   // sigue el hilo
```
El modo **agéntico** expone **herramientas internas** sobre la base
(`search_knowledge_base`, `list_domains`, `list_labels`, `find_domains`, `find_labels`,
`get_document_chunks`, `get_adjacent_chunk`, `create_domain`, `create_label`,
`ingest_document`, `create_profile`, `create_guardrail`) vía function-calling, y **también
pasa por el enrutado y los guardarails** (entrada antes del bucle de tools, salida sobre la
respuesta final).

`get_document_chunks`/`get_adjacent_chunk` dejan que el modelo pida el resto de un
documento cuando una cita se corta a media frase: cada chunk lleva un `ChunkIndex`
(su posición 0-based dentro del `source`, asignado en la ingesta) que permite pedir
"todos los chunks de este documento" o "el siguiente/anterior a este". No añaden ningún
método nuevo a `IVectorStore` — ambas pagínan `ListChunksAsync` (ya público) y resuelven
en memoria, porque el orden de paginación es específico de cada backend y no se puede
asumir correlacionado con `ChunkIndex`.

`find_domains`/`find_labels` rankean los dominios/etiquetas ya definidos por similitud
semántica de su `Description` frente a una consulta (mismo embedder que ingesta/búsqueda),
para que el modelo pueda descubrir cuáles pasarle luego a `search_knowledge_base` o
`ingest_document` cuando hay demasiados como para razonarlo leyendo `list_domains`/
`list_labels` a pelo. Calculan el embedding de cada descripción en cada llamada — simple
y correcto, a costa de latencia proporcional al tamaño del catálogo; si eso pesa, cachear
esos vectores e invalidar en `Define*Async`/`Remove*Async` es la mejora natural (no incluida
en esta primera versión).

`search_knowledge_base` queda **fijo al dominio enrutado del turno** — un `domain` que el
modelo intente colar en sus propios argumentos se ignora, salvo que actives
`AgentToolScope.CrossDomainSearch`: entonces el tool acepta un `domain` explícito por
llamada (para saltar a otro dominio si el enrutado no era el correcto) y, si lo omite,
busca en **todos** los dominios sin filtrar. Es el único flag que no añade un tool nuevo:
cambia el *schema* y el comportamiento de `search_knowledge_base` en sí.

## MCP (paquete `RagKit.Mcp`)
Conecta servidores **MCP** externos por stdio y registra sus herramientas en el
mismo bucle de agente (cliente JSON-RPC propio, sin SDK):
```csharp
await rag.AddStdioServerAsync("npx", "-y", "@modelcontextprotocol/server-everything", "stdio");
// ahora AskAgentAsync puede usar también las tools del servidor MCP
```
O por **configuración**: habilita `McpServers.Enable()` y declara los servidores en
`RagOptions.Mcps` (una línea de comando por servidor); `CreateAsync` los conecta solos:
```csharp
McpServers.Enable();
var opts = new RagOptions { /* … */ };
opts.Mcps.Add("npx -y @modelcontextprotocol/server-everything stdio");
```
El adaptador (`IMcpConnection`/`McpTool`) está testeado offline; el e2e contra un
servidor real es opt-in (`RAGKIT_MCP_CMD`/`RAGKIT_MCP_ARGS`).

## Base vectorial y embeddings (intercambiables)
La base está **detrás de una interfaz** (`IVectorStore`) con un **selector por enum**
y un **factory** — contrato idéntico para todos los backends:

```csharp
Store = new StoreConfig { Kind = VectorStoreKind.Qdrant, Url = "http://127.0.0.1:6333" }
// VectorStoreKind: InMemory (default) · Qdrant · SqlServer · Postgres
```
Los **cuatro** backends están **implementados y testeados contra servicios reales** (Docker):
- **InMemory** (default, cero setup), **Qdrant** (REST), **Postgres + pgvector** (`RagKit.Postgres`) y **SQL Server 2025** (tipo `VECTOR`, `RagKit.SqlServer`).
- El factory es un **registry**: los conectores en paquete aparte se enchufan con su `Enable()` (`PostgresStore.Enable();`, `SqlServerStore.Enable();`), manteniendo el core sin dependencias de BBDD.

El **embedder** también va tras contrato (`IEmbedder`) + factory (`EmbedderKind`):
- **Local** (hash, cero setup) por defecto.
- **ONNX local** (`RagKit.Onnx`, `OnnxEmbedding.Enable()`): modelo sentence-transformers
  in-process — pooling (mean o CLS, según el modelo) + L2; semántico y privado.
  `EmbedderConfig.Model` = carpeta con `model.onnx` y el tokenizer: **WordPiece**
  (`vocab.txt`, p. ej. all-MiniLM-L6-v2) o **SentencePiece**
  (`sentencepiece.bpe.model`/`spiece.model`/`tokenizer.model`, p. ej. multilingual-e5
  o BGE-M3). Tres presets zero-config (descargan y cachean el modelo la primera vez):
  - `UseDefaultModelAsync()` — all-MiniLM-L6-v2, inglés, 384-dim.
  - `UseMultilingualDefaultModelAsync()` — multilingual-e5-small, ~100 idiomas incl.
    español, 384-dim. Antepone automáticamente el prefijo `query:`/`passage:` que
    esta familia de modelos exige (asimétrico: sin él, la calidad de recuperación
    se degrada de forma sistemática — no es opcional, es parte del contrato del modelo).
  - `UseBgeM3DefaultModelAsync()` — BGE-M3 (solo su embedding denso; no expone
    sparse/ColBERT), 1024-dim, más grande y con mejor discriminación semántica que
    e5-small en corpus multilingües, a cambio de más peso/latencia en CPU. No
    necesita prefijo. Pooling CLS resuelto automáticamente (o tomado directamente
    de una segunda salida ya pooleada del grafo ONNX, si el export la expone).
- **API compatible OpenAI** (`EmbedderKind.OpenAi`, de fábrica): sirve para OpenAI y
  para **servidores locales como Ollama**. P. ej. **Ollama + nomic-embed-text**:
  ```csharp
  Embedder = new EmbedderConfig {
      Kind = EmbedderKind.OpenAi,
      Url = "http://localhost:11434/v1",
      Model = "nomic-embed-text",
      // Dimension = 768  // opcional; si no, se sondea al iniciar
  }
  ```
- **A medida**: implementa `IEmbedder` (`ModelId`, `Dimension`, `EmbedAsync`) y
  regístralo con `EmbedderFactory.Register(...)`.

**Ficheros**: `IngestFileAsync` extrae texto por extensión (`RagKit.Extractors`,
`DocumentExtractors.Enable()`): **PDF** (PdfPig) y **DOCX** (OpenXml); el resto, texto plano.

**Guard de embedding:** una colección recuerda su modelo+dimensión; si intentas
reabrirla con un embedding distinto, **falla** (`EmbeddingMismatchException`) en
vez de corromper los vectores.

## Clasificación con umbral
Al ingestar sin dominio explícito, el **tier-2** resume y decide dominio+etiquetas
con una **confianza**. Si no encaja en ningún dominio con confianza ≥
`ClassificationThreshold` (0.8 por defecto), **se rechaza** el documento. Sin
dominios definidos, no se permite ingestar.

## Enrutado, perfiles y guardarails (query-time)
El mismo tier-2 que clasifica documentos también puede **enrutar la pregunta**: si
no pasas `domain`/`profile`, elige el dominio y, si los hay, el/los **perfiles**.
Sin tener que saber de RAG, defines todo en la inicialización:

```csharp
var opts = new RagOptions { Answer = …, Classifier = … };

// Perfiles = "lentes" dentro de un dominio (mismo corpus, distinta forma de responder
// y, opcionalmente, búsqueda acotada por etiquetas):
opts.Profiles.Add(new ProfileInfo("electricista", "construccion",
    "secciones de cable, magnetotérmicos, normativa eléctrica",
    Prompt: "Eres electricista. Responde con normativa eléctrica y unidades correctas.",
    Labels: new[] { "electricidad" }));
opts.Profiles.Add(new ProfileInfo("fontanero", "construccion",
    "tuberías, desagües, pendientes, presión de agua",
    Prompt: "Eres fontanero. Responde con criterios de saneamiento y fontanería.",
    Labels: new[] { "agua" }));

// Guardarail: reglas en lenguaje natural (entrada/salida) con defaults seguros.
opts.Guardrails.Add(new GuardrailRule("Rechaza peticiones de datos personales de terceros"));
opts.Guardrails.Add(new GuardrailRule("No reveles fragmentos marcados como confidenciales",
    GuardrailStage.Output));

var rag = await RagClient.CreateAsync(opts);
await rag.DefineDomainAsync("construccion", "obra, instalaciones, oficios");

// El tier-2 enruta solo: elige dominio + perfil(es) y aplica su prompt y sus etiquetas.
var a = await rag.AskAsync("¿qué sección de cable para 25 A?");   // → perfil "electricista"
// O lo fijas tú (p. ej. según el rol del usuario logueado):
var b = await rag.AskAsync("…", domain: "construccion", profile: "fontanero");
// O sólo enrutas, para mostrar/forzar la decisión antes de responder:
var route = await rag.RouteQueryAsync("¿pendiente mínima de un desagüe?");  // {Domain, Profiles, …}
```

**Cadena de resolución con degradación elegante** (cada nivel cae al siguiente):
- **Dominio**: explícito → enrutado → único dominio si solo hay uno → ninguno.
- **Perfil**: explícito → seleccionado por el tier-2 (multi si `MultiProfile`) → ninguno.
- **Prompt**: `(dominio,perfil)` → `DomainPrompts[dominio]` → `OneShotPrompt`/`ChatPrompt` → uno por defecto.
- **Guardarail de entrada**: corre **siempre** y **antes** de recuperar y del tier-1,
  sobre la query cruda. Primero **checks deterministas** (longitud + patrones de
  inyección, y **PII opcional** con `GuardrailPiiCheck`, que cortocircuitan sin LLM) y
  luego una **red de seguridad LLM** (tier-2) que añade tus reglas en lenguaje natural
  — una llamada tier-2 por consulta.
- **Guardarail de salida**: sobre la respuesta; solo actúa si hay reglas de salida. En
  **streaming**, una regla de salida aplicable hace que la respuesta se **bufferice,
  se valide y se emita de golpe** (no se pueden "des-emitir" tokens) — el streaming en
  vivo solo se degrada cuando hay reglas de salida configuradas.

Todo es conmutable (`EnableQueryRouting`, `EnableInputGuardrail`, `EnableOutputGuardrail`)
y, en un chat con memoria, el enrutado se resuelve **solo en el primer turno** y se reutiliza.

**CRUD en runtime + persistencia.** Perfiles y guardarails se definen en `RagOptions`
(semilla) **o** en caliente y quedan **persistidos en el store** (los 4 backends),
sobreviviendo a reinicios:
```csharp
await rag.DefineProfileAsync(new ProfileInfo("fontanero", "construccion",
    "tuberías, desagües", Prompt: "Eres fontanero…", Labels: new[] { "agua" }));
await rag.DefineGuardrailAsync(new GuardrailRule("Rechaza datos de terceros"));
await rag.RemoveProfileAsync("fontanero", "construccion");
var perfiles = await rag.ListProfilesAsync();
```

**Prompts editables en caliente.** `OneShotPrompt`/`ChatPrompt`/`DomainPrompts`
se pueden leer y mutar directamente sobre el `RagClient` ya creado — el cambio
se aplica en la siguiente pregunta, sin recrear el cliente (a diferencia de
perfiles/guardarails, esto **no** se persiste en el store; vive en memoria
del proceso):
```csharp
rag.OneShotPrompt = "Eres un asistente muy formal.";
rag.SetDomainPrompt("fiscal", "Eres un asesor fiscal.");
rag.RemoveDomainPrompt("fiscal");
```

## Gestión de documentos
Más allá de ingestar, RagKit sabe **borrar, listar, reingestar sin duplicar y
recorrer una carpeta**, con el mismo contrato en los 4 backends:

```csharp
// Borrado por source (opcionalmente acotado a un dominio; sin él, borra en todos).
int borrados = await rag.RemoveDocumentAsync("iva.txt", domain: "fiscal");

// Inventario: un DocumentInfo por source, agregando los chunks internos.
var docs = await rag.ListDocumentsAsync(domain: "fiscal");
foreach (var d in docs) Console.WriteLine($"{d.Source}: {d.ChunkCount} chunks, {d.IngestedAtUtc:u}");

// Ingesta idempotente: si el contenido no cambió desde la última vez, no hace nada
// (ni clasifica ni embebe); si cambió, sustituye los chunks anteriores del mismo source.
var r = await rag.IngestFileIfChangedAsync("iva.txt", domain: "fiscal");
// r.Outcome: Ingested (nuevo o cambiado) | Unchanged (no-op barato) | Rejected

// Carpeta completa: un IngestResult por fichero según se completa (progreso incremental).
await foreach (var res in rag.IngestFolderAsync("./docs", domain: "fiscal", recursive: true))
    Console.WriteLine($"{res.Source}: {res.Outcome}");

// Borrado de un dominio completo: sus chunks, la propia definición del dominio,
// y en cascada los perfiles/guardarailes que solo aplicaban a ese dominio
// (los globales y los de otros dominios no se tocan).
var borrado = await rag.RemoveDomainAsync("fiscal"); // DomainRemovalResult(Existed, RemovedChunks)

// Listado paginado de los chunks de un documento (no carga la colección entera).
string? cursor = null;
do
{
    var page = await rag.ListChunksAsync("iva.txt", domain: "fiscal", take: 50, cursor: cursor);
    foreach (var chunk in page.Items) Console.WriteLine($"{chunk.Id}: {chunk.Text[..30]}…");
    cursor = page.NextCursor;
} while (cursor is not null);
```

**Catálogo genérico**: además de perfiles/guardarails (tipados), el store expone un
almacén *key-value* libre para que la aplicación guarde sus propios metadatos (el
manifiesto de `IngestIfChangedAsync` vive aquí) sin forkear el backend:
```csharp
await rag.SaveCatalogEntryAsync("app-config", "feature-flags", "{\"betaUi\":true}");
var json = await rag.GetCatalogEntryAsync("app-config", "feature-flags");
await rag.DeleteCatalogEntryAsync("app-config", "feature-flags");
```
`kind`/`key` son de libre elección del consumidor; RagKit no los interpreta. Un store
que no implemente el catálogo (p. ej. uno propio de terceros) simplemente no persiste
nada (no-op), sin romper el contrato.

## Panel de mantenimiento (`RagKit.Dashboard`)
Paquete opt-in con un panel mínimo de administración (al estilo del dashboard de
Qdrant o el de Hangfire) sobre la API pública de `RagClient`. Se monta con una línea:
```csharp
builder.Services.AddSingleton(rag); // tu RagClient ya creado
// ...
app.MapRagDashboard(path: "/rag-admin").RequireAuthorization("AdminOnly");
```
**Sin autenticación propia por defecto** — `MapRagDashboard` devuelve un
`IEndpointConventionBuilder` para que cuelgues tu propio esquema de auth de ASP.NET
Core; no lo expongas público sin ello. Solo target `net10.0` (acoplado a ASP.NET
Core). Trae CRUD completo de **dominios, etiquetas, documentos, chunks
paginados, guardarails, perfiles y prompts**, **ingesta con seguimiento de
progreso** (vía Server-Sent Events) y un **playground de preguntas**
(`AskAsync`/`AskStreamAsync`, citas antes que los tokens) — ver
[`src/RagKit.Dashboard/README.md`](src/RagKit.Dashboard/README.md).

## Estado y roadmap
**Hecho y testeado:**
- ✅ Fachada `RagClient` (`CreateAsync`), dos tiers, dominios/etiquetas, prompts (one-shot/chat) configurables en Markdown.
- ✅ `IVectorStore` + enum + factory; **InMemory** y **Qdrant** reales; guard de modelo/dimensión persistido.
- ✅ Auto-clasificación con **confianza + umbral + rechazo**.
- ✅ **Enrutado en query-time + perfiles (lentes) + guardarails** (entrada siempre activo; salida también en streaming vía buffer), aplicados también en el **modo agéntico**; **CRUD + persistencia** de perfiles/guardarails en los 4 backends.
- ✅ Cliente chat compatible OpenAI (solo `HttpClient`); recuperación acotada por dominio/etiquetas; troceado por frontera.
- ✅ **Gestión de documentos**: borrado por `source` (`RemoveDocumentAsync`), borrado de dominio completo (`RemoveDomainAsync`), inventario agregado (`ListDocumentsAsync`), listado paginado de chunks por documento con id (`ListChunksAsync`), ingesta idempotente por hash (`IngestIfChangedAsync`/`IngestFileIfChangedAsync`, con `IngestOutcome.Unchanged`), ingesta de carpeta completa (`IngestFolderAsync`) y catálogo genérico key-value (`Get/Save/DeleteCatalogEntryAsync`) — los 4 backends.
- ✅ **Ask multi-turno con historial explícito** (`AskAsync`/`AskStreamAsync` con `priorHistory`): función pura sin estado interno compartido, alternativa a `ChatSession` para consumidores que persisten su propio historial y necesitan sobrevivir a reinicios del proceso.
- ✅ **Modo agéntico streameable** (`AskAgentStreamAsync`): tokens de la respuesta final más eventos de actividad de herramienta (`ToolCallStarted`/`ToolCallFinished`), citas antes del primer token, mismos enrutado/guardarails/`AgentToolScope` que la versión no streameada.
- ✅ **Prompts editables en caliente** sobre el `RagClient` ya creado (`OneShotPrompt`/`ChatPrompt`/`DomainPrompts`), sin recrear el cliente.
- ✅ **`RagKit.Dashboard`** — panel de mantenimiento opt-in: montaje, auth hook,
  CRUD completo, ingesta con seguimiento de progreso (SSE) y playground de
  preguntas (`AskAsync`/`AskStreamAsync`, SSE); empaquetado (assets embebidos
  verificados) y suite de tests propia cerrados.
- ✅ **Herramientas agénticas ampliadas**: `find_domains`/`find_labels` (descubrimiento
  semántico de dominios/etiquetas), `get_document_chunks`/`get_adjacent_chunk`
  (navegación por `ChunkIndex`, sin tocar `IVectorStore`) y `AgentToolScope.CrossDomainSearch`
  (le da a `search_knowledge_base` un `domain` explícito por llamada, en vez de quedar
  fijo al dominio enrutado del turno) — persistido en los 4 backends.

**Recuperación híbrida + reranking:** por defecto fusiona **vector denso + BM25
léxico** con **RRF** (`Hybrid=true`), para encontrar tanto sinónimos como términos
literales (códigos, "art. 14", años); el índice BM25 vive en RagKit y se reconstruye
del store al iniciar. Un **reranker** opcional (`IReranker` + `SetReranker`) re-puntúa
los candidatos antes del top-k. Dos opciones ya incluidas:
- `RagKit.Onnx`'s **`OnnxCrossEncoderReranker`** (cross-encoder local, p. ej.
  `ms-marco-MiniLM`, que puntúa cada par pregunta/pasaje) — WordPiece/inglés
  por ahora; en corpus no ingleses conviene priorizar un embedder multilingüe
  (`UseMultilingualDefaultModelAsync()`, arriba) antes que forzar este
  reranker, ya que un cross-encoder fuera de su idioma puede empeorar el
  orden en vez de mejorarlo.
- **`RagOptions.EnableLlmRerank`** — sin modelo local: le pide al tier-2 (el
  mismo que ya clasifica/enruta/guardarraila) que reordene los candidatos.
  Multilingüe gratis y sin descarga, a cambio de una llamada tier-2 extra por
  pregunta (una sola, no una por candidato). Opt-in, `false` por defecto.

**Hecho además:** embeddings **ONNX locales** (`RagKit.Onnx`, con
`UseDefaultModelAsync()` en inglés, `UseMultilingualDefaultModelAsync()` para
~100 idiomas incl. español y `UseBgeM3DefaultModelAsync()` como alternativa
multilingüe más grande — los tres zero-config, descargan y cachean el modelo)
y **por API/Ollama** (`nomic-embed-text`), **4 conectores reales**
(InMemory/Qdrant/Postgres/SQL Server 2025),
**bucle de agente** + herramientas internas + **MCP externo** (`RagKit.Mcp`),
**extractores PDF/DOCX** (`RagKit.Extractors`), **streaming de respuestas** (SSE),
**robustez** (reintentos/timeouts), **CI**, y **empaquetado NuGet listo para publicar**
(multi-target `net8.0`/`net10.0`, README por paquete, Source Link y símbolos `.snupkg`;
publicación automatizada por tag `v*`).

**Ejemplos** (Blazor Server, los tres con Ollama `qwen2.5:7b` + `nomic-embed-text`):
- [`examples/MiniRag`](examples/MiniRag) — un dominio, subida de fichero, "lentes"
  de perfil y prompt editable en caliente. `dotnet run --project examples/MiniRag` → `http://localhost:5117`.
- [`examples/RagSimple`](examples/RagSimple) — el arranque mínimo de verdad: ingerir
  texto + preguntar, sin nada más; LLM/embedder configurables por `appsettings.json`.
  `dotnet run --project examples/RagSimple` → `http://localhost:5118`.
- [`examples/RagCompleto`](examples/RagCompleto) — el tour completo: 3 dominios con
  auto-clasificación, perfiles, guardarails + PII check, chat con memoria y modo
  agente, página de ingesta, y **`RagKit.Dashboard` montado en `/rag-admin`**.
  `dotnet run --project examples/RagCompleto` → `http://localhost:5119`.

**Conocido, post-1.0:** la CI actual corre la suite offline (los tests de
integración contra Qdrant/SQL Server/Postgres se auto-omiten si el servicio no
está disponible, no hay `docker-compose` en el workflow) — validarlos de
verdad en CI, más observabilidad (logging/métricas), quedan como siguiente
paso; no bloquean 1.0 porque la superficie pública ya es estable (ver
CHANGELOG: un solo breaking change documentado en toda la serie 0.x).

## Diseño
Fachada simple por fuera; capas limpias por dentro. Los adaptadores que cambian
rápido (LLM, MCP, embeddings) viven tras interfaces (`IEmbedder`, `IChatClient`),
así el motor interno se sustituye sin romper a quien lo usa.

📐 **Diagramas** (pipeline de consulta, ingesta y componentes): [`docs/architecture.md`](docs/architecture.md).

## Build
```bash
dotnet test   # 72 tests del core (net8.0 y net10.0) + 21 de RagKit.Dashboard (net10.0), sin red
```
