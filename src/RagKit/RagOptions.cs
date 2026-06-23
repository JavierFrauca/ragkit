namespace RagKit;

/// <summary>An OpenAI-compatible LLM endpoint (OpenAI, DeepSeek, Groq, local…).</summary>
public sealed class LlmConfig
{
    public string Url { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";

    /// <summary>
    /// Tiempo máximo (segundos) de la petición HTTP al LLM. Por defecto 300: los
    /// modelos locales (Ollama, vLLM…) pueden tardar bastante en "pensar" sobre
    /// contextos grandes. Súbelo si ves cancelaciones por timeout.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(Model);
}

/// <summary>
/// Everything a consumer configures. The goal: a developer who knows nothing
/// about embeddings or vector databases sets up two LLMs, defines a few
/// domains/labels, drops in documents and asks questions.
/// </summary>
public sealed class RagOptions
{
    /// <summary>Tier-1 LLM: the strong model that writes the answers.</summary>
    public LlmConfig Answer { get; set; } = new();

    /// <summary>
    /// Tier-2 LLM: a cheaper/faster model used to classify documents (pick the
    /// domain and labels) on ingest. If null, the Answer model is reused.
    /// </summary>
    public LlmConfig? Classifier { get; set; }

    /// <summary>Vector store selection (InMemory by default). One enum switches backend.</summary>
    public StoreConfig Store { get; set; } = new();

    /// <summary>Embedding model selection (Local by default; ONNX is the intended real default).</summary>
    public EmbedderConfig Embedder { get; set; } = new();

    /// <summary>System prompt (Markdown) for one-shot <c>AskAsync</c>. Null → citation-aware default.</summary>
    public string? OneShotPrompt { get; set; }

    /// <summary>System prompt (Markdown) for <c>StartChat</c>. Null → falls back to <see cref="OneShotPrompt"/> or the default.</summary>
    public string? ChatPrompt { get; set; }

    /// <summary>How many retrieved chunks to feed the answer model.</summary>
    public int TopK { get; set; } = 5;

    /// <summary>
    /// Hybrid retrieval: fuse dense (vector) with lexical (BM25) via RRF, so both
    /// semantic matches and literal terms (codes, "art. 14", years) are found.
    /// On by default.
    /// </summary>
    public bool Hybrid { get; set; } = true;

    /// <summary>
    /// When true (default) and domains are defined, the tier-2 model decides the
    /// domain and labels of each ingested document automatically.
    /// </summary>
    public bool AutoClassify { get; set; } = true;

    /// <summary>
    /// Minimum classification confidence (0..1) to accept a document into a
    /// domain. Below this, ingestion is rejected as "no corresponde a ningún
    /// dominio". Default 0.8.
    /// </summary>
    public double ClassificationThreshold { get; set; } = 0.8;

    /// <summary>MCP server endpoints to expose as tools. Reserved for a later increment.</summary>
    public IList<string> Mcps { get; } = new List<string>();
}
