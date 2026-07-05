# RagSimple — el arranque mínimo, de verdad

El ejemplo más pequeño posible de RagKit: un dominio, una caja para ingerir
texto y una caja para preguntar. Sin perfiles, sin subida de fichero, sin
panel de administración — para eso está [`RagCompleto`](../RagCompleto).

## El RAG, literalmente, en 4 líneas
```csharp
var rag = await RagClient.CreateAsync(new RagOptions
{
    Answer   = new LlmConfig { Url = "http://localhost:11434/v1", Model = "qwen2.5:7b" },
    Embedder = new EmbedderConfig { Kind = EmbedderKind.OpenAi, Url = "http://localhost:11434/v1", Model = "nomic-embed-text", Dimension = 768 },
    AutoClassify = false,
});
```
A diferencia de [`MiniRag`](../MiniRag), aquí el LLM/embedder **no están en
código**: vienen de `appsettings.json` (sección `"Rag"`), así que apuntar a
DeepSeek/OpenAI/otro Ollama es editar un fichero, no recompilar.

## Cómo ejecutarlo
```bash
# 1) Instala Ollama y descarga los modelos (una sola vez; los mismos que MiniRag)
ollama pull qwen2.5:7b
ollama pull nomic-embed-text

# 2) Arranca el ejemplo
dotnet run --project examples/RagSimple
# abre http://localhost:5118
```
Para usar un LLM en la nube (DeepSeek, OpenAI…) en vez de Ollama, edita
`appsettings.json` con la URL/modelo/clave de tu proveedor.

## Qué verás
- **Panel ①** — pega texto: se trocea, se vectoriza y se indexa en el único
  dominio del ejemplo (`"documentos"`).
- **Panel ②** — pregunta y recibe la respuesta **en streaming** con **citas**
  a los fragmentos usados.

## Dos opciones de una línea (comentadas en `Program.cs`)
- `EnableLlmRerank = true` — el propio tier-2 reordena los resultados por
  relevancia antes de responder. Sin descargar ningún modelo.
- `Embedder = await RagKit.Onnx.OnnxEmbedding.UseMultilingualDefaultModelAsync()`
  — si la búsqueda semántica en castellano con Ollama no basta, descarga y
  cachea un modelo ONNX multilingüe la primera vez (requiere referenciar
  `RagKit.Onnx`).

`RagClient` se registra como singleton ya inicializado en `Program.cs` (sin
esperar a la primera visita a la página) y se inyecta directamente en
`Home.razor` — no hay ninguna clase intermedia que envuelva la API.
