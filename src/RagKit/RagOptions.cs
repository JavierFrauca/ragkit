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

    /// <summary>
    /// Whether this model supports the OpenAI JSON function-calling protocol
    /// (<c>tool_choice: auto</c>). Default null → true (OpenAI-compatible assumption).
    /// Set to false for models that ignore the <c>tools</c> parameter and write tool
    /// calls as XML in the content stream instead — the agent loop then falls back to
    /// one-shot RAG (pre-retrieves documents and injects them as context, no tool calls).
    /// </summary>
    public bool? SupportsTools { get; set; }

    /// <summary>
    /// When true, <see cref="IChatClient.NextStreamAsync"/> detects tool calls written
    /// by the model as XML in the content stream (e.g. <c>&lt;search_knowledge_base&gt;
    /// {"query":"…"}&lt;/search_knowledge_base&gt;</c>) and translates them into
    /// <see cref="AgentDelta"/> events as if they were standard OpenAI
    /// <c>delta.tool_calls</c>. Designed for reasoning models (e.g. deepseek-v4-pro)
    /// that write XML tool calls in the content stream instead of using the JSON
    /// function-calling protocol. Default null → false.
    /// </summary>
    public bool? ParseXmlToolCalls { get; set; }
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

    /// <summary>
    /// MCP servers to connect at startup, each a stdio command line
    /// (e.g. "npx -y @modelcontextprotocol/server-everything stdio"). Requires the
    /// RagKit.Mcp package with <c>McpServers.Enable()</c>; otherwise configuring any
    /// entry throws. Their tools join the agent loop alongside the internal ones.
    /// </summary>
    public IList<string> Mcps { get; } = new List<string>();

    // --- query-time routing, profiles and guardrails --------------------------

    /// <summary>
    /// Profiles ("lenses"/sub-views) per domain, defined at init. Each carries an
    /// optional focused prompt and an optional label set to scope retrieval. The
    /// tier-2 model selects one (or several, see <see cref="MultiProfile"/>) from
    /// the query when no profile is passed explicitly.
    /// </summary>
    public IList<ProfileInfo> Profiles { get; } = new List<ProfileInfo>();

    /// <summary>
    /// Optional per-domain system prompt. Sits between the (domain,profile) prompt
    /// and the global <see cref="OneShotPrompt"/>/<see cref="ChatPrompt"/> in the
    /// prompt-resolution chain.
    /// </summary>
    public IDictionary<string, string> DomainPrompts { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Guardrail rules (input and/or output), defined at init. The input guardrail
    /// runs even with no rules (deterministic checks + a built-in LLM safety net);
    /// rules add their natural-language constraints to that same tier-2 call.
    /// </summary>
    public IList<GuardrailRule> Guardrails { get; } = new List<GuardrailRule>();

    /// <summary>
    /// When true (default) and a query arrives without an explicit domain/profile,
    /// the tier-2 model routes it (picks the domain and profile[s]). Routing runs
    /// only if there is more than one domain, or any profiles are defined.
    /// </summary>
    public bool EnableQueryRouting { get; set; } = true;

    /// <summary>Below this routing confidence (0..1), the query degrades to the safety
    /// net: no domain filter (or the single domain), no profile, global prompt. Default 0.5.</summary>
    public double RoutingThreshold { get; set; } = 0.5;

    /// <summary>Allow the router to select several profiles for one query (their labels are
    /// fused for retrieval). On by default — good for wide trees with overlapping branches.</summary>
    public bool MultiProfile { get; set; } = true;

    /// <summary>
    /// Input guardrail over the raw query, before retrieval and the tier-1 model.
    /// On by default and always active: deterministic checks (length + injection)
    /// plus a built-in LLM safety net, so it adds one tier-2 call per query. Set to
    /// false to disable it entirely.
    /// </summary>
    public bool EnableInputGuardrail { get; set; } = true;

    /// <summary>
    /// Output guardrail over the generated answer. On by default, but a no-op (no LLM
    /// call) unless output rules are defined — there is no universal output default.
    /// In streaming, an applicable output rule makes the answer buffer-then-emit
    /// (it can't validate already-sent tokens).
    /// </summary>
    public bool EnableOutputGuardrail { get; set; } = true;

    /// <summary>Message returned when a guardrail blocks a query or answer. Configurable.</summary>
    public string GuardrailRejectionMessage { get; set; } = "Lo siento, no puedo ayudar con esa petición.";

    /// <summary>Deterministic input cap: queries longer than this are rejected. 0 disables it. Default 4000.</summary>
    public int MaxQueryLength { get; set; } = 4000;

    /// <summary>
    /// Deterministic PII check on the raw query (email, phone, IBAN, card, DNI/NIF).
    /// Off by default: it would otherwise reject legitimate questions that merely
    /// mention an email/number. Turn it on to block queries that carry personal data.
    /// </summary>
    public bool GuardrailPiiCheck { get; set; } = false;

    /// <summary>
    /// When true, the tier-2 model reorders retrieved candidates by relevance before
    /// the top-k truncation (one extra tier-2 call per query — off by default). Unlike
    /// a local cross-encoder (see <c>RagKit.Onnx</c>'s <c>OnnxCrossEncoderReranker</c>),
    /// this needs no model download and is multilingual for free (same model already
    /// used for classification/routing/guardrails). A reranker installed explicitly via
    /// <c>RagClient.SetReranker</c> always takes precedence over this.
    /// </summary>
    public bool EnableLlmRerank { get; set; } = false;

    /// <summary>
    /// Override for the chunk character budget, taking precedence over <see
    /// cref="IEmbedder.MaxChunkChars"/> when set. Null (default) defers to the
    /// active embedder's own value.
    /// </summary>
    public int? ChunkMaxChars { get; set; }

    /// <summary>
    /// When true, ingest generates a 3-line tier-2 summary of the document and
    /// prepends it (plus the chunk's heading breadcrumb) to the text that gets
    /// EMBEDDED — never to what's persisted/cited. Off by default: costs one extra
    /// tier-2 call per document and shrinks the effective per-chunk character budget
    /// to leave room for the prefix.
    /// </summary>
    public bool EnableContextualEmbedding { get; set; } = false;

    /// <summary>
    /// Bound (seconds) on each best-effort tier-2 enrichment call made when <see
    /// cref="EnableContextualEmbedding"/> is on (document summary, oversized-table
    /// explanation) — independent of <see cref="LlmConfig.TimeoutSeconds"/>, which
    /// governs required tier-2 calls like classification. These calls are optional
    /// context for the embedding: if the tier-2 endpoint is slow or unavailable,
    /// ingestion proceeds without the contextual prefix rather than blocking on
    /// <see cref="LlmConfig.TimeoutSeconds"/> (× 3 retries) per document. Default 20.
    /// </summary>
    public int ContextualEmbeddingTimeoutSeconds { get; set; } = 20;
}
