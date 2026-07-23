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

    /// <summary>
    /// <b>Perfiles automáticos (query routing)</b>. Cuando está activo, antes de responder
    /// el tier-2 mira la pregunta y elige una "lente" (perfil) que ajusta el tono. Cuesta
    /// una llamada extra al LLM por pregunta; desactívalo para usar solo el prompt editable.
    /// </summary>
    public bool AutoProfiles { get; set; } = true;

    // Reglas de grounding comunes a todas las lentes (cada lente reemplaza el system prompt,
    // así que debe traer sus propias reglas de citado/anti-alucinación).
    private const string GroundingRules =
        "Responde ÚNICAMENTE con la información de los fragmentos recuperados. " +
        "Cita las fuentes como [1], [2]… Si la respuesta no está en los documentos, dilo y no la inventes. " +
        "Responde en español.";

    /// <summary>
    /// Las "lentes" (perfiles) del dominio: el tier-2 elige una según la pregunta. Aquí solo
    /// cambian el tono (mismo dominio, distinta forma de responder). Un perfil también podría
    /// mapear a etiquetas para acotar la búsqueda; en este ejemplo de texto libre no lo hacen.
    /// </summary>
    public static readonly ProfileInfo[] Lenses =
    {
        new("conciso",   Domain, "preguntas que piden una respuesta breve y directa",
            Prompt: GroundingRules + " Sé muy breve: una o dos frases, al grano, sin rodeos."),
        new("detallado", Domain, "preguntas que piden explicación amplia, contexto o matices técnicos",
            Prompt: GroundingRules + " Sé exhaustivo: explica el contexto, los matices y los detalles técnicos relevantes."),
    };

    /// <summary>True cuando el cliente está listo (Ollama respondió y se cargó el modelo).</summary>
    public bool Ready => _client is not null;

    /// <summary>Último error de arranque, para mostrarlo en la UI si Ollama no está disponible.</summary>
    public string? Error { get; private set; }

    /// <summary>Documentos ingeridos en esta sesión (solo para listarlos en pantalla).</summary>
    public List<IngestedDoc> Docs { get; } = new();

    // ─────────────────────────────────────────────────────────────────────────
    //  EL RAG EN 4 LÍNEAS  (el resto del fichero es UI/estado del ejemplo)
    // ─────────────────────────────────────────────────────────────────────────
    private RagOptions BuildOptions()
    {
        var opts = new RagOptions
        {
            // 1) El LLM que redacta las respuestas (Ollama, modelo qwen2.5:7b).
            //    TimeoutSeconds alto: un 7B en local (sobre todo en CPU) puede tardar
            //    minutos en contextos grandes; así no se cancela la respuesta.
            Answer = new LlmConfig { Url = OllamaUrl, Model = "qwen2.5:7b", TimeoutSeconds = 600 },
            // 2) El modelo de embeddings (Ollama, nomic-embed-text → 768 dimensiones).
            Embedder = new EmbedderConfig { Kind = EmbedderKind.OpenAi, Url = OllamaUrl, Model = "nomic-embed-text", Dimension = 768 },
            // 3) Sin auto-clasificación: en este ejemplo todo va a un único dominio.
            AutoClassify = false,
            // 4) El prompt de sistema (Markdown). Aquí, el de ejemplo editable.
            OneShotPrompt = SystemPrompt,
            // 5) Una lente por pregunta (tono), no varias a la vez.
            MultiProfile = false,
            // 6) (Opcional) el propio tier-2 reordena los resultados por relevancia
            //    antes de responder — un booleano, sin descargar ningún modelo:
            // EnableLlmRerank = true,
        };
        // Perfiles ("lentes"): el tier-2 elige uno según la pregunta (query routing).
        foreach (var lens in Lenses) opts.Profiles.Add(lens);
        // El guardarail de entrada va activo por defecto: primero checks deterministas
        // (longitud + patrones de inyección, que bloquean "ignora las instrucciones…"
        // sin gastar LLM) y luego una red de seguridad LLM (una llamada al tier-2 por
        // pregunta). Para reglas a medida: opts.Guardrails.Add(new GuardrailRule("…")).
        return opts;
    }
    // (El almacén vectorial es InMemory por defecto: cero instalación. Para
    //  persistencia real basta cambiar Store por Qdrant/Postgres/SQL Server.)

    /// <summary>Inicializa el cliente y crea el dominio. Se llama una vez al cargar la página.</summary>
    public async Task InitializeAsync()
    {
        if (_client is not null) return;
        try
        {
            _client = await RagClient.CreateAsync(BuildOptions());
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
    /// Enruta la pregunta: si los perfiles automáticos están activos, el tier-2 elige
    /// la lente más adecuada (o null si no hay confianza). Devuelve su nombre para
    /// mostrarlo en la UI; luego se pasa a <see cref="AskStream"/>.
    /// </summary>
    public async Task<string?> RouteAsync(string question)
    {
        if (_client is null || !AutoProfiles) return null;
        var route = await _client.RouteQueryAsync(question);
        return route.Profiles.Count > 0 ? route.Profiles[0] : null;
    }

    /// <summary>
    /// Pregunta sobre los documentos con la lente ya elegida. Devuelve un
    /// <see cref="RagStream"/>: las citas están listas al instante y la respuesta llega
    /// token a token (streaming). El guardarail de entrada se aplica dentro de RagKit.
    /// </summary>
    public Task<RagStream> AskStream(string question, string? profile)
    {
        // El prompt es editable en caliente sobre el propio RagClient (RagClient.OneShotPrompt):
        // RagKit lo relee en cada llamada, así no hay que recrear el cliente ni retener el
        // RagOptions original aparte. Cuando hay perfil, su prompt-lente tiene prioridad sobre
        // este (cadena de resolución de prompt).
        _client!.OneShotPrompt = string.IsNullOrWhiteSpace(SystemPrompt) ? null : SystemPrompt;
        // Pasamos dominio y perfil explícitos: el enrutado ya se hizo en RouteAsync,
        // así evitamos una segunda llamada al tier-2.
        return _client!.AskStreamAsync(question, domain: Domain, profile: profile);
    }

    /// <summary>Restaura el prompt de ejemplo (botón de la UI).</summary>
    public void ResetPrompt() => SystemPrompt = DefaultPrompt;

    public int ChunkCount => Docs.Sum(d => d.Chunks);
}

/// <summary>Un documento ingerido (nombre + nº de fragmentos indexados).</summary>
public sealed record IngestedDoc(string Source, int Chunks);
