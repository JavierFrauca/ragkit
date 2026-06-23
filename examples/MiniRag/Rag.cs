using RagKit;

namespace MiniRag;

/// <summary>
/// El corazón del ejemplo: un RAG completo sobre tus documentos en, literalmente,
/// cuatro líneas de configuración (mira <see cref="CreateClientAsync"/>).
///
/// Modelos: <b>todo local con Ollama</b>, sin claves ni nube.
///   • LLM de respuesta  → <c>qwen2.5:7b</c>
///   • Embeddings        → <c>nomic-embed-text</c> (vectores de 768 dimensiones)
///
/// Arranca Ollama y descarga los modelos una sola vez:
///   <code>
///   ollama pull qwen2.5:7b
///   ollama pull nomic-embed-text
///   </code>
///
/// Esta clase es un singleton de Blazor que guarda el cliente ya inicializado y
/// expone dos operaciones: <see cref="IngestAsync"/> (meter documentos) y
/// <see cref="AskStream"/> (preguntar con respuesta en streaming + citas).
/// </summary>
public sealed class Rag
{
    // Endpoint OpenAI-compatible que expone Ollama en local. El mismo host sirve
    // tanto el LLM de chat como el modelo de embeddings.
    private const string OllamaUrl = "http://localhost:11434/v1";

    // Un único "dominio" donde caen todos los documentos del ejemplo. RagKit exige
    // al menos un dominio definido; al ingerir indicándolo explícitamente nos
    // saltamos el clasificador (este mini-RAG no necesita rutear por temas).
    public const string Domain = "documentos";

    private RagClient? _client;
    private RagOptions? _options;

    /// <summary>
    /// Prompt de sistema (en Markdown) que se le pasa al LLM. RagKit lo admite vía
    /// <c>RagOptions.OneShotPrompt</c> (y <c>ChatPrompt</c> para el modo chat); si se
    /// deja vacío, RagKit usa uno por defecto orientado a citar fuentes. Editable
    /// desde la UI: se aplica en la siguiente pregunta sin recrear el cliente.
    /// </summary>
    public string SystemPrompt { get; set; } = DefaultPrompt;

    /// <summary>Un prompt de ejemplo, para enseñar que se puede personalizar.</summary>
    public const string DefaultPrompt =
        "Eres el asistente de una base documental.\n" +
        "- Responde ÚNICAMENTE con la información de los fragmentos recuperados.\n" +
        "- Cita las fuentes usando [1], [2]… según el orden en que aparecen.\n" +
        "- Si la respuesta no está en los documentos, dilo con claridad y no la inventes.\n" +
        "- Responde en español, de forma clara y concisa.";

    /// <summary>True cuando el cliente está listo (Ollama respondió y se cargó el modelo).</summary>
    public bool Ready => _client is not null;

    /// <summary>Último error de arranque, para mostrarlo en la UI si Ollama no está disponible.</summary>
    public string? Error { get; private set; }

    /// <summary>Documentos ingeridos en esta sesión (solo para listarlos en pantalla).</summary>
    public List<IngestedDoc> Docs { get; } = new();

    // ─────────────────────────────────────────────────────────────────────────
    //  EL RAG EN 4 LÍNEAS  (el resto del fichero es UI/estado del ejemplo)
    // ─────────────────────────────────────────────────────────────────────────
    private RagOptions BuildOptions() => new()
    {
        // 1) El LLM que redacta las respuestas (Ollama, modelo qwen2.5:7b).
        //    TimeoutSeconds alto: un 7B en local (sobre todo en CPU) puede tardar
        //    minutos en contextos grandes; así no se cancela la respuesta.
        Answer   = new LlmConfig { Url = OllamaUrl, Model = "qwen2.5:7b", TimeoutSeconds = 600 },
        // 2) El modelo de embeddings (Ollama, nomic-embed-text → 768 dimensiones).
        Embedder = new EmbedderConfig { Kind = EmbedderKind.OpenAi, Url = OllamaUrl, Model = "nomic-embed-text", Dimension = 768 },
        // 3) Sin auto-clasificación: en este ejemplo todo va a un único dominio.
        AutoClassify = false,
        // 4) El prompt de sistema (Markdown). Aquí, el de ejemplo editable.
        OneShotPrompt = SystemPrompt,
    };
    // (El almacén vectorial es InMemory por defecto: cero instalación. Para
    //  persistencia real basta cambiar Store por Qdrant/Postgres/SQL Server.)

    /// <summary>Inicializa el cliente y crea el dominio. Se llama una vez al cargar la página.</summary>
    public async Task InitializeAsync()
    {
        if (_client is not null) return;
        try
        {
            _options = BuildOptions();
            _client = await RagClient.CreateAsync(_options);
            await _client.DefineDomainAsync(Domain, "Documentos subidos por el usuario.");
            Error = null;
        }
        catch (Exception ex)
        {
            // Lo habitual: Ollama no arrancado o modelos sin descargar.
            Error = ex.Message;
            _client = null;
        }
    }

    /// <summary>Ingiere texto: se trocea, se vectoriza con nomic-embed-text y se indexa.</summary>
    public async Task<bool> IngestAsync(string source, string text)
    {
        if (_client is null || string.IsNullOrWhiteSpace(text)) return false;
        var r = await _client.IngestAsync(text, source, domain: Domain);
        if (r.Rejected) return false;
        Docs.Add(new IngestedDoc(source, r.ChunkCount));
        return true;
    }

    /// <summary>Ingiere un fichero (PDF/DOCX/txt) extrayendo su texto automáticamente.</summary>
    public async Task<bool> IngestFileAsync(string path, string displayName)
    {
        if (_client is null) return false;
        var r = await _client.IngestFileAsync(path, domain: Domain);
        if (r.Rejected) return false;
        Docs.Add(new IngestedDoc(displayName, r.ChunkCount));
        return true;
    }

    /// <summary>
    /// Pregunta sobre los documentos. Devuelve un <see cref="RagStream"/>: las citas
    /// están listas al instante y la respuesta llega token a token (streaming).
    /// </summary>
    public Task<RagStream> AskStream(string question)
    {
        // El prompt es editable en caliente: lo aplicamos en cada pregunta. RagKit
        // lo lee en cada llamada, así no hay que recrear el cliente.
        _options!.OneShotPrompt = string.IsNullOrWhiteSpace(SystemPrompt) ? null : SystemPrompt;
        return _client!.AskStreamAsync(question, domain: Domain);
    }

    /// <summary>Restaura el prompt de ejemplo (botón de la UI).</summary>
    public void ResetPrompt() => SystemPrompt = DefaultPrompt;

    public int ChunkCount => Docs.Sum(d => d.Chunks);
}

/// <summary>Un documento ingerido (nombre + nº de fragmentos indexados).</summary>
public sealed record IngestedDoc(string Source, int Chunks);
