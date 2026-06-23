using MiniRag;
using MiniRag.Components;

var builder = WebApplication.CreateBuilder(args);

// Blazor Server (interactivo en servidor): la forma más simple de tener UI.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Habilita los extractores de documentos (PDF/DOCX). Una sola línea: a partir
// de aquí RagClient.IngestFileAsync sabe leer esos formatos.
RagKit.Extractors.DocumentExtractors.Enable();

// El RAG vive como singleton compartido (un usuario, local). Toda la magia
// está en Rag.cs ("el RAG en 4 líneas").
builder.Services.AddSingleton<Rag>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
