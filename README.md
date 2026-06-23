# RagKit

**RAG agéntico llave en mano.** Para el desarrollador que tiene que "montar un
RAG" y no quiere pelearse con embeddings, bases vectoriales ni pipelines. Añade
el paquete, configura **dos LLM**, define tus **dominios y etiquetas**, suelta
documentos (se clasifican solos) y pregunta.

```csharp
using RagKit;

var rag = new RagClient(new RagOptions
{
    // Tier-1: el modelo bueno que redacta las respuestas
    Answer     = new LlmConfig { Url = "https://api.deepseek.com", ApiKey = "sk-…", Model = "deepseek-chat" },
    // Tier-2: uno más barato/rápido que clasifica los documentos al ingestarlos
    Classifier = new LlmConfig { Url = "https://api.deepseek.com", ApiKey = "sk-…", Model = "deepseek-chat" },
});

// Defines tu estructura (con descripciones que guían al tier-2)
rag.DefineDomain("fiscal", "impuestos, IVA, IRPF, tributos")
   .DefineDomain("rrhh", "personal, nóminas, contratos")
   .DefineLabel("iva").DefineLabel("irpf").DefineLabel("contrato");

// Ingestas: el tier-2 decide dominio y etiquetas (o los pasas tú a mano)
var r = await rag.IngestAsync("El tipo general del IVA es del 21%…", source: "iva.txt");
// r.Domain == "fiscal", r.Labels == ["iva"]

// Preguntas: el tier-1 responde, acotado al dominio y con citas
var a = await rag.AskAsync("¿Cuál es el IVA general?", domain: "fiscal");
Console.WriteLine(a.Answer);
foreach (var c in a.Citations) Console.WriteLine($"  · [{c.Source}] {c.Snippet}");
```

No configuras modelo de embeddings ni base de datos: **se resuelve por ti**.

## Lo que configuras
| Opción | Para qué |
|---|---|
| `Answer` | LLM **tier-1** (compatible OpenAI) que redacta respuestas. |
| `Classifier` | LLM **tier-2** que clasifica documentos (dominio + etiquetas). Si lo omites, se reusa `Answer`. |
| `SystemPrompt` *(opc.)* | Prompt del RAG. Por defecto, uno con citas. |
| `TopK` *(opc.)* | Fragmentos de contexto (5 por defecto). |
| `AutoClassify` *(opc.)* | Si el tier-2 clasifica solo en la ingesta (on por defecto). |

Cualquier endpoint **compatible OpenAI** vale: OpenAI, DeepSeek, Groq, Mistral, o
local con Ollama/vLLM/LM Studio (solo cambia `Url`+`ApiKey`).

## Formas de usarlo
```csharp
var a = await rag.AskAsync("…", domain: "fiscal");        // 1) RAG one-shot (cualquier modelo)
var chat = rag.StartChat(domain: "fiscal");                // 2) Chat con memoria
var g = await rag.AskAgentAsync("…", domain: "fiscal");    // 3) Agéntico: el modelo decide
//        buscar / crear dominios·etiquetas / ingestar / llamar MCP (requiere modelo con tools;
//        si no, cae a one-shot automáticamente)

// 4) Streaming (token a token). Las citas están listas antes de generar.
var s = await rag.AskStreamAsync("…", domain: "fiscal");
await foreach (var token in s.Tokens) Console.Write(token);   // s.Citations ya disponible
// chat.AskStreamAsync(...) hace lo mismo en una sesión con memoria.
```
El modo **agéntico** expone **herramientas internas** sobre la base
(`search_knowledge_base`, `list_domains`, `list_labels`, `create_domain`,
`create_label`, `ingest_document`) vía function-calling.

## MCP (paquete `RagKit.Mcp`)
Conecta servidores **MCP** externos por stdio y registra sus herramientas en el
mismo bucle de agente (cliente JSON-RPC propio, sin SDK):
```csharp
await rag.AddStdioServerAsync("npx", "-y", "@modelcontextprotocol/server-everything", "stdio");
// ahora AskAgentAsync puede usar también las tools del servidor MCP
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
  (p. ej. all-MiniLM-L6-v2) in-process — tokenizer BERT + mean-pooling + L2; semántico
  y privado. `EmbedderConfig.Model` = carpeta con `model.onnx` + `vocab.txt`.
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

## Estado y roadmap
**Hecho y testeado:**
- ✅ Fachada `RagClient` (`CreateAsync`), dos tiers, dominios/etiquetas, prompts (one-shot/chat) configurables en Markdown.
- ✅ `IVectorStore` + enum + factory; **InMemory** y **Qdrant** reales; guard de modelo/dimensión persistido.
- ✅ Auto-clasificación con **confianza + umbral + rechazo**.
- ✅ Cliente chat compatible OpenAI (solo `HttpClient`); recuperación acotada por dominio/etiquetas; troceado por frontera.

**Recuperación híbrida + reranking:** por defecto fusiona **vector denso + BM25
léxico** con **RRF** (`Hybrid=true`), para encontrar tanto sinónimos como términos
literales (códigos, "art. 14", años); el índice BM25 vive en RagKit y se reconstruye
del store al iniciar. Un **reranker** opcional (`IReranker` + `SetReranker`) re-puntúa
los candidatos antes del top-k.

**Hecho además:** embeddings **ONNX locales** (`RagKit.Onnx`) y **por API/Ollama**
(`nomic-embed-text`), **4 conectores reales** (InMemory/Qdrant/Postgres/SQL Server 2025),
**bucle de agente** + herramientas internas + **MCP externo** (`RagKit.Mcp`),
**extractores PDF/DOCX** (`RagKit.Extractors`), **streaming de respuestas** (SSE),
**robustez** (reintentos/timeouts), **CI** y **paquetes NuGet**.

**Ejemplo:** [`examples/MiniRag`](examples/MiniRag) — un RAG local en 4 líneas con
Ollama (`qwen2.5:7b` + `nomic-embed-text`): ingiere documentos y chatea sobre ellos.
```bash
dotnet run --project examples/MiniRag   # http://localhost:5117
```

**Siguiente (opcional):** modelos ONNX multilingües (tokenizer SentencePiece),
un reranker cross-encoder de fábrica, publicar en NuGet.org.

## Diseño
Fachada simple por fuera; capas limpias por dentro. Los adaptadores que cambian
rápido (LLM, MCP, embeddings) viven tras interfaces (`IEmbedder`, `IChatClient`),
así el motor interno se sustituye sin romper a quien lo usa.

## Build
```bash
dotnet test   # 6 tests, sin red
```
