# MiniRag — un RAG local en 4 líneas

Ejemplo operativo y mínimo de RagKit: **ingiere documentos** y **chatea sobre
ellos**, 100 % en local con [Ollama](https://ollama.com) (sin claves, sin nube).

- **LLM de respuesta:** `qwen2.5:7b`
- **Embeddings:** `nomic-embed-text` (768 dimensiones)
- **Almacén vectorial:** en memoria (cero instalación)

## El RAG, literalmente, en 4 líneas
```csharp
var rag = await RagClient.CreateAsync(new RagOptions
{
    Answer   = new LlmConfig { Url = "http://localhost:11434/v1", Model = "qwen2.5:7b" },
    Embedder = new EmbedderConfig { Kind = EmbedderKind.OpenAi, Url = "http://localhost:11434/v1", Model = "nomic-embed-text", Dimension = 768 },
    AutoClassify = false,
});
```
(todo el código vive comentado en [`Rag.cs`](Rag.cs))

## Cómo ejecutarlo
```bash
# 1) Instala Ollama y descarga los modelos (una sola vez)
ollama pull qwen2.5:7b
ollama pull nomic-embed-text

# 2) Arranca el ejemplo
dotnet run --project examples/MiniRag
# abre http://localhost:5117
```

## Qué verás
- **Panel ①** — pega texto o sube un PDF/DOCX/TXT: se trocea, se vectoriza y se indexa.
- **Panel ②** — pregunta y recibe la respuesta **en streaming** (token a token) con
  **citas** a los fragmentos usados. Incluye un **prompt de sistema editable en
  caliente** (se aplica en la siguiente pregunta, sin recrear el cliente).

## Además: enrutado de perfiles y guardarail
El panel del prompt incluye un interruptor **«Perfiles automáticos»**: cuando está
activo, antes de responder el tier-2 elige una **lente** (`conciso` / `detallado`)
según la pregunta y ajusta el tono; la respuesta muestra qué lente se usó. Los
perfiles se definen en [`Rag.cs`](Rag.cs) (`ProfileInfo`) con su propio prompt.
Si lo **apagas**, no se enruta y manda el prompt de sistema editable de arriba
(la *degradación elegante* de la cadena de resolución).

El **guardarail de entrada** va activo de fábrica: checks deterministas que bloquean
intentos de manipular las instrucciones (p. ej. «ignora las instrucciones…») sin LLM,
más una red de seguridad LLM (una llamada al tier-2 por pregunta). Para reglas en
lenguaje natural basta con `opts.Guardrails.Add(new GuardrailRule("…"))`.
