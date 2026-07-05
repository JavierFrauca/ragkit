using RagKit;
using RagKit.Dashboard;
using RagCompleto;
using RagCompleto.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Habilita los extractores de documentos (PDF/DOCX) para la página de ingesta.
RagKit.Extractors.DocumentExtractors.Enable();

var settings = builder.Configuration.GetSection("Rag").Get<RagSettings>() ?? new RagSettings();
var options = new RagOptions
{
    Answer = new LlmConfig { Url = settings.Answer.Url, Model = settings.Answer.Model, TimeoutSeconds = 600 },
    // Sin Classifier explícito: se reutiliza Answer como tier-2 (auto-clasificación
    // y enrutado no necesitan un modelo distinto para este ejemplo).
    Embedder = new EmbedderConfig
    {
        Kind = EmbedderKind.OpenAi,
        Url = settings.Embedder.Url,
        Model = settings.Embedder.Model,
        Dimension = settings.Embedder.Dimension,
    },
    // Auto-clasificación real: con 3 dominios definidos, el tier-2 decide dónde
    // cae cada documento y a qué dominio/perfil enruta cada pregunta.
    AutoClassify = true,
    MultiProfile = false, // un perfil por dominio en este ejemplo: no hay solape que fusionar
    GuardrailPiiCheck = true, // bloquea preguntas que lleven datos personales (email, IBAN, DNI…)
    // Reranker de segunda etapa: el propio tier-2 reordena los candidatos por
    // relevancia (una llamada extra por pregunta) en vez de un cross-encoder local.
    // Un solo booleano — sin descargar ningún modelo, y multilingüe gratis porque
    // reusa el mismo tier-2 que ya clasifica/enruta/guardarraila en castellano.
    EnableLlmRerank = true,
};

// Alternativa al embedder de Ollama de arriba, para corpus donde la búsqueda
// semántica en castellano necesite más calidad: descarga y cachea un modelo
// ONNX multilingüe la primera vez (RagKit.Onnx), sin tocar nada más:
//   options.Embedder = await RagKit.Onnx.OnnxEmbedding.UseMultilingualDefaultModelAsync();

// Un perfil ("lente") por dominio — el tier-2 lo selecciona al enrutar la pregunta.
options.Profiles.Add(new ProfileInfo("asesor", Domains.Fiscal, "asesoría fiscal y tributaria",
    Prompt: "Eres un asesor fiscal. Responde con precisión sobre impuestos, IVA e IRPF, citando las fuentes."));
options.Profiles.Add(new ProfileInfo("gestor", Domains.Rrhh, "gestión de personal y nóminas",
    Prompt: "Eres un gestor de RRHH. Responde sobre contratos, nóminas y normativa laboral, citando las fuentes."));
options.Profiles.Add(new ProfileInfo("abogado", Domains.Legal, "asesoría legal y cumplimiento",
    Prompt: "Eres un abogado. Responde sobre contratos y cumplimiento normativo con precisión, citando las fuentes."));

// Guardarails: uno de entrada, uno de salida — además del PII check determinista de arriba.
options.Guardrails.Add(new GuardrailRule("Rechaza peticiones que pidan datos personales de terceros"));
options.Guardrails.Add(new GuardrailRule("No reveles información marcada como confidencial en los documentos",
    GuardrailStage.Output));

var rag = await RagClient.CreateAsync(options);
foreach (var (name, description) in Domains.All)
    await rag.DefineDomainAsync(name, description);
builder.Services.AddSingleton(rag);

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Panel de administración (CRUD, ingesta con progreso, playground) montado
// aparte de la UI de consumidor — sin autenticación propia (ver el README de
// RagKit.Dashboard); en un despliegue real, encadena aquí tu propio esquema:
// .RequireAuthorization("AdminOnly").
app.MapRagDashboard(path: "/rag-admin");

app.Run();
