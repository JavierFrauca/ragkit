using RagKit;
using RagSimple;
using RagSimple.Components;

var builder = WebApplication.CreateBuilder(args);

// Blazor Server (interactivo en servidor): la forma más simple de tener UI.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ─────────────────────────────────────────────────────────────────────────
//  EL RAG EN 4 LÍNEAS — LLM/embedder vienen de appsettings.json (sección
//  "Rag"), no de constantes en código: cambia el fichero para apuntar a
//  DeepSeek/OpenAI/otro Ollama, sin tocar una sola línea de C#.
// ─────────────────────────────────────────────────────────────────────────
var settings = builder.Configuration.GetSection("Rag").Get<RagSettings>() ?? new RagSettings();
var rag = await RagClient.CreateAsync(new RagOptions
{
    Answer = new LlmConfig { Url = settings.Answer.Url, Model = settings.Answer.Model, TimeoutSeconds = 600 },
    Embedder = new EmbedderConfig
    {
        Kind = EmbedderKind.OpenAi,
        Url = settings.Embedder.Url,
        Model = settings.Embedder.Model,
        Dimension = settings.Embedder.Dimension,
    },
    // Un único dominio, sin clasificador: no hace falta enrutar ni clasificar
    // cuando todo el contenido cae en el mismo sitio.
    AutoClassify = false,
    // Un booleano más y el propio tier-2 reordena los resultados por relevancia
    // antes de responder (sin descargar ningún modelo de rerank aparte):
    //   EnableLlmRerank = true,
});
// Alternativa al Embedder de arriba si la búsqueda en castellano no basta:
// descarga y cachea un modelo ONNX multilingüe la primera vez (ver RagKit.Onnx),
// en vez de pasar EmbedderConfig como arriba:
//   Embedder = await RagKit.Onnx.OnnxEmbedding.UseMultilingualDefaultModelAsync(),
await rag.DefineDomainAsync(Rag.Domain, "Documentos subidos por el usuario.");
// (Sin este await previo a AddSingleton, Blazor no tendría un RagClient listo
//  para inyectar en la primera petición — ver examples/RagCompleto para el
//  mismo patrón con varios dominios y el Dashboard montado encima.)
builder.Services.AddSingleton(rag);

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
