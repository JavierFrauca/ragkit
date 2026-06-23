using System.Runtime.CompilerServices;
using System.Text;
using RagKit.Agent;
using RagKit.Internal;

namespace RagKit;

/// <summary>
/// The whole RAG in one object. Configure two LLMs (tier-1 answers, tier-2
/// classification), pick a vector store and an embedder (sane defaults), define
/// domains and labels, ingest documents (the tier-2 model routes each into a
/// domain with a confidence threshold) and ask questions (tier-1, grounded and
/// cited). Create it with <see cref="CreateAsync"/>.
/// </summary>
public sealed class RagClient
{
    private readonly RagOptions _options;
    private readonly IEmbedder _embedder;
    private readonly IVectorStore _store;
    private readonly IChatClient _answer;
    private readonly Classifier _classifier;
    private readonly List<IRagTool> _externalTools = new();
    private readonly LexicalIndex _lexical = new();
    private readonly SemaphoreSlim _lexicalGate = new(1, 1);
    private bool _lexicalLoaded;
    private IReranker? _reranker;

    internal RagClient(RagOptions options, IEmbedder embedder, IVectorStore store, IChatClient answer, IChatClient classifier)
    {
        _options = options;
        _embedder = embedder;
        _store = store;
        _answer = answer;
        _classifier = new Classifier(classifier);
    }

    /// <summary>
    /// Build and initialize a client from options. Wires the embedder and store
    /// from their factories and runs the model/dimension guard on the store.
    /// </summary>
    public static async Task<RagClient> CreateAsync(RagOptions options, CancellationToken ct = default)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        var embedder = EmbedderFactory.Create(options.Embedder);
        var store = VectorStoreFactory.Create(options.Store);
        var answer = BuildChat(options.Answer, "Answer (tier-1)");
        var classifier = options.Classifier is { IsConfigured: true } c2 ? BuildChat(c2, "Classifier (tier-2)") : answer;
        var client = new RagClient(options, embedder, store, answer, classifier);
        // Async one-time embedder init (probe dimension / load model) before the
        // store's model+dimension guard runs — no blocking work in constructors.
        await embedder.InitializeAsync(ct).ConfigureAwait(false);
        await store.InitializeAsync(embedder.ModelId, embedder.Dimension, ct).ConfigureAwait(false);
        // The lexical index for hybrid search is built lazily on first retrieval
        // (see EnsureLexicalAsync), so startup stays cheap even on large stores.
        return client;
    }

    /// <summary>The store backing this client (for the internal data-access tools).</summary>
    public IVectorStore Store => _store;

    /// <summary>Install an optional second-stage reranker (applied after RRF fusion).</summary>
    public void SetReranker(IReranker reranker) => _reranker = reranker;

    private static IChatClient BuildChat(LlmConfig cfg, string which)
    {
        if (!cfg.IsConfigured)
            throw new RagKitException($"El LLM '{which}' no está configurado (faltan Url/Model).");
        return new OpenAiChatClient(cfg.Url, cfg.ApiKey, cfg.Model, timeoutSeconds: cfg.TimeoutSeconds);
    }

    // --- structure -----------------------------------------------------------

    public Task<DomainInfo> DefineDomainAsync(string name, string description = "", CancellationToken ct = default)
        => _store.CreateDomainAsync(name, description, ct);

    public Task<LabelInfo> DefineLabelAsync(string name, string description = "", CancellationToken ct = default)
        => _store.CreateLabelAsync(name, description, ct);

    public Task<IReadOnlyList<DomainInfo>> ListDomainsAsync(CancellationToken ct = default) => _store.ListDomainsAsync(ct);
    public Task<IReadOnlyList<LabelInfo>> ListLabelsAsync(CancellationToken ct = default) => _store.ListLabelsAsync(ct);
    public Task<int> ChunkCountAsync(CancellationToken ct = default) => _store.CountAsync(ct);

    // --- ingest --------------------------------------------------------------

    /// <summary>
    /// Ingest text. Requires at least one domain to be defined. If no explicit
    /// <paramref name="domain"/> is given and auto-classify is on, the tier-2
    /// model picks the domain/labels; if it doesn't match any domain with
    /// confidence ≥ the configured threshold, the document is rejected.
    /// </summary>
    public async Task<IngestResult> IngestAsync(
        string text, string? source = null, string? domain = null,
        IEnumerable<string>? labels = null, CancellationToken ct = default)
    {
        source ??= "doc";
        var domains = await _store.ListDomainsAsync(ct).ConfigureAwait(false);
        if (domains.Count == 0)
            return new IngestResult(source, null, Array.Empty<string>(), 0, Rejected: true,
                Reason: "No hay dominios definidos: define al menos uno antes de ingestar.");

        var finalLabels = new List<string>(labels ?? Enumerable.Empty<string>());
        double confidence = 1.0;

        if (domain is null && _options.AutoClassify)
        {
            var allLabels = await _store.ListLabelsAsync(ct).ConfigureAwait(false);
            var excerpt = text.Length > 1500 ? text[..1500] : text;
            var (d, ls, conf) = await _classifier.ClassifyAsync(excerpt, domains, allLabels, ct).ConfigureAwait(false);
            domain = d;
            confidence = conf;
            foreach (var l in ls) if (!finalLabels.Contains(l)) finalLabels.Add(l);

            if (domain is null || confidence < _options.ClassificationThreshold)
                return new IngestResult(source, domain, finalLabels, 0, Rejected: true,
                    Reason: $"No corresponde a ningún dominio con confianza suficiente " +
                            $"({confidence:0.00} < {_options.ClassificationThreshold:0.00}).",
                    Confidence: confidence);
        }
        else if (domain is not null &&
                 !domains.Any(x => string.Equals(x.Name, domain, StringComparison.OrdinalIgnoreCase)))
        {
            return new IngestResult(source, domain, finalLabels, 0, Rejected: true,
                Reason: $"El dominio '{domain}' no está definido.");
        }

        var labelArr = finalLabels.ToArray();
        var chunks = Chunker.Chunk(text);
        if (chunks.Count == 0)
            return new IngestResult(source, domain, labelArr, 0, Confidence: confidence);

        // Batch the embeddings (one round-trip / parallel inference) and the writes.
        var vectors = await _embedder.EmbedBatchAsync(chunks, ct).ConfigureAwait(false);
        var batch = new List<EmbeddedChunk>(chunks.Count);
        for (int i = 0; i < chunks.Count; i++)
            batch.Add(new EmbeddedChunk(source, chunks[i], domain, labelArr, vectors[i]));
        await _store.AddChunksAsync(batch, ct).ConfigureAwait(false);

        // Keep the lexical index in sync only once it's been loaded; otherwise the
        // first retrieval will enumerate these chunks from the store anyway.
        if (_options.Hybrid && Volatile.Read(ref _lexicalLoaded))
            foreach (var c in batch) _lexical.Add(new StoredChunk(c.Source, c.Text, c.Domain, c.Labels));

        return new IngestResult(source, domain, labelArr, chunks.Count, Confidence: confidence);
    }

    /// <summary>Ingest a file. Text is extracted via <see cref="FileExtractors"/>
    /// (PDF/DOCX when RagKit.Extractors is enabled; plain text otherwise).</summary>
    public Task<IngestResult> IngestFileAsync(string path, string? domain = null,
        IEnumerable<string>? labels = null, CancellationToken ct = default)
        => IngestAsync(FileExtractors.Extract(path), Path.GetFileName(path), domain, labels, ct);

    // --- ask -----------------------------------------------------------------

    /// <summary>One-shot RAG: retrieve, build the grounded prompt, answer with citations. Works with any model.</summary>
    public async Task<RagAnswer> AskAsync(
        string question, string? domain = null, IReadOnlyList<string>? labels = null, CancellationToken ct = default)
    {
        var hits = await RetrieveAsync(question, domain, labels, ct).ConfigureAwait(false);
        var messages = new[]
        {
            new ChatMessage("system", _options.OneShotPrompt ?? Prompt.DefaultSystem),
            new ChatMessage("user", Prompt.BuildUser(question, hits)),
        };
        var answer = await _answer.CompleteAsync(messages, ct).ConfigureAwait(false);
        return new RagAnswer(answer, ToCitations(hits));
    }

    /// <summary>
    /// One-shot RAG, streamed: retrieve, then stream the grounded answer token-by-token.
    /// <see cref="RagStream.Citations"/> are ready immediately; enumerate
    /// <see cref="RagStream.Tokens"/> to consume the answer as it arrives.
    /// </summary>
    public async Task<RagStream> AskStreamAsync(
        string question, string? domain = null, IReadOnlyList<string>? labels = null, CancellationToken ct = default)
    {
        var hits = await RetrieveAsync(question, domain, labels, ct).ConfigureAwait(false);
        var messages = new[]
        {
            new ChatMessage("system", _options.OneShotPrompt ?? Prompt.DefaultSystem),
            new ChatMessage("user", Prompt.BuildUser(question, hits)),
        };
        return new RagStream(ToCitations(hits), _answer.StreamAsync(messages, ct));
    }

    /// <summary>Start a multi-turn chat (optionally scoped to a domain), grounded per turn.</summary>
    public ChatSession StartChat(string? domain = null)
        => new(this, _options.ChatPrompt ?? _options.OneShotPrompt ?? Prompt.DefaultSystem, domain);

    /// <summary>Register an external tool (e.g. an MCP server adapter) for the agent loop.</summary>
    public void RegisterTool(IRagTool tool) => _externalTools.Add(tool);

    /// <summary>
    /// Agentic answer: the model decides when to search the knowledge base, list
    /// or create domains/labels, ingest, or call external (MCP) tools — looping
    /// until it answers. Requires a tool-capable model; otherwise it transparently
    /// falls back to one-shot <see cref="AskAsync"/>.
    /// </summary>
    public async Task<RagAnswer> AskAgentAsync(string question, string? domain = null, int maxSteps = 5, CancellationToken ct = default)
    {
        if (!_answer.SupportsTools)
            return await AskAsync(question, domain, null, ct).ConfigureAwait(false);

        var search = new SearchTool(this, domain);
        var tools = new List<IRagTool>
        {
            search, new ListDomainsTool(this), new ListLabelsTool(this),
            new CreateDomainTool(this), new CreateLabelTool(this), new IngestTool(this, domain),
        };
        tools.AddRange(_externalTools);
        var specs = tools.Select(t => new ToolSpec(t.Name, t.Description, t.ParametersSchema)).ToList();
        var byName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

        var msgs = new List<AgentMessage>
        {
            new("system", _options.OneShotPrompt ?? Prompt.AgentSystem),
            new("user", question),
        };

        for (int step = 0; step < maxSteps; step++)
        {
            var turn = await _answer.NextAsync(msgs, specs, ct).ConfigureAwait(false);
            if (turn.ToolCalls.Count == 0)
                return new RagAnswer(turn.Content ?? "", search.Citations);

            msgs.Add(new AgentMessage("assistant", turn.Content, ToolCalls: turn.ToolCalls));
            foreach (var call in turn.ToolCalls)
            {
                string result;
                try
                {
                    result = byName.TryGetValue(call.Name, out var tool)
                        ? await tool.InvokeAsync(call.ArgumentsJson, ct).ConfigureAwait(false)
                        : $"error: herramienta '{call.Name}' desconocida";
                }
                catch (Exception ex) { result = "error: " + ex.Message; }
                msgs.Add(new AgentMessage("tool", result, ToolCallId: call.Id));
            }
        }

        // Out of steps: force a final answer without tools.
        var final = await _answer.CompleteAsync(
            msgs.Where(m => m.Role is "system" or "user" or "assistant")
                .Select(m => new ChatMessage(m.Role, m.Content ?? "")).ToList(), ct).ConfigureAwait(false);
        return new RagAnswer(final, search.Citations);
    }

    // --- internals shared with ChatSession ----------------------------------

    private async Task<float[]> Embed(string text, CancellationToken ct) => await _embedder.EmbedAsync(text, ct).ConfigureAwait(false);

    /// <summary>Central retrieval: vector search, optional hybrid RRF fusion with
    /// the BM25 lexical index, optional rerank, then top-k.</summary>
    internal async Task<IReadOnlyList<StoredHit>> RetrieveAsync(string query, string? domain, IReadOnlyList<string>? labels, CancellationToken ct)
    {
        int k = _options.TopK;
        int fetch = Math.Max(k * 5, 20);
        var vector = await _store.SearchAsync(await Embed(query, ct), fetch, domain, labels, ct).ConfigureAwait(false);

        List<StoredHit> fused;
        if (_options.Hybrid)
        {
            await EnsureLexicalAsync(ct).ConfigureAwait(false);
            fused = RrfFuse(vector, _lexical.Search(query, fetch, domain, labels));
        }
        else fused = vector.ToList();

        if (_reranker is not null)
        {
            int window = Math.Min(fused.Count, Math.Max(k, 20));
            return await _reranker.RerankAsync(query, fused.Take(window).ToList(), k, ct).ConfigureAwait(false);
        }
        return fused.Count > k ? fused.GetRange(0, k) : fused;
    }

    /// <summary>Load the in-memory lexical (BM25) index from the store once, on first
    /// hybrid retrieval. Single-node: the index lives in this process. See README.</summary>
    private async Task EnsureLexicalAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _lexicalLoaded)) return;
        await _lexicalGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_lexicalLoaded) return;
            foreach (var c in await _store.EnumerateAsync(ct).ConfigureAwait(false))
                _lexical.Add(c);
            Volatile.Write(ref _lexicalLoaded, true);
        }
        finally { _lexicalGate.Release(); }
    }

    /// <summary>Reciprocal Rank Fusion of the dense and lexical rankings (keyed by source+text).</summary>
    private static List<StoredHit> RrfFuse(IReadOnlyList<StoredHit> vector, IReadOnlyList<(StoredChunk Chunk, double Score)> lexical)
    {
        const double rrfK = 60.0;
        var scores = new Dictionary<string, double>();
        var rep = new Dictionary<string, StoredHit>();
        static string Key(string source, string text) => source + "" + text;

        for (int i = 0; i < vector.Count; i++)
        {
            var key = Key(vector[i].Source, vector[i].Text);
            scores[key] = scores.GetValueOrDefault(key) + 1.0 / (rrfK + i + 1);
            rep.TryAdd(key, vector[i]);
        }
        for (int i = 0; i < lexical.Count; i++)
        {
            var c = lexical[i].Chunk;
            var key = Key(c.Source, c.Text);
            scores[key] = scores.GetValueOrDefault(key) + 1.0 / (rrfK + i + 1);
            rep.TryAdd(key, new StoredHit(c.Source, c.Text, c.Domain, c.Labels, 0));
        }
        return scores.OrderByDescending(kv => kv.Value)
            .Select(kv => rep[kv.Key] with { Score = kv.Value })
            .ToList();
    }

    internal Task<string> CompleteAnswerAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
        => _answer.CompleteAsync(messages, ct);

    internal IAsyncEnumerable<string> StreamAnswerAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
        => _answer.StreamAsync(messages, ct);

    internal static IReadOnlyList<Citation> ToCitations(IReadOnlyList<StoredHit> hits)
    {
        var list = new List<Citation>(hits.Count);
        foreach (var h in hits) list.Add(new Citation(h.Source, Snippet(h.Text), h.Score));
        return list;
    }

    private static string Snippet(string text, int max = 200) => text.Length <= max ? text : text[..max] + "…";
}

/// <summary>A conversational session that augments each user turn with retrieval.</summary>
public sealed class ChatSession
{
    private readonly RagClient _rag;
    private readonly string? _domain;
    private readonly List<ChatMessage> _history = new();

    internal ChatSession(RagClient rag, string systemPrompt, string? domain)
    {
        _rag = rag;
        _domain = domain;
        _history.Add(new ChatMessage("system", systemPrompt));
    }

    public async Task<RagAnswer> AskAsync(string message, CancellationToken ct = default)
    {
        var hits = await _rag.RetrieveAsync(message, _domain, null, ct).ConfigureAwait(false);
        _history.Add(new ChatMessage("user", Internal.Prompt.BuildUser(message, hits)));
        var answer = await _rag.CompleteAnswerAsync(_history.ToArray(), ct).ConfigureAwait(false);
        _history.Add(new ChatMessage("assistant", answer));
        return new RagAnswer(answer, RagClient.ToCitations(hits));
    }

    /// <summary>Streamed turn: grounds on retrieval, streams the answer, and records
    /// the full assistant turn in history once the stream completes.</summary>
    public async Task<RagStream> AskStreamAsync(string message, CancellationToken ct = default)
    {
        var hits = await _rag.RetrieveAsync(message, _domain, null, ct).ConfigureAwait(false);
        _history.Add(new ChatMessage("user", Internal.Prompt.BuildUser(message, hits)));
        return new RagStream(RagClient.ToCitations(hits), StreamAndRecord(ct));
    }

    private async IAsyncEnumerable<string> StreamAndRecord([EnumeratorCancellation] CancellationToken ct)
    {
        var sb = new StringBuilder();
        await foreach (var piece in _rag.StreamAnswerAsync(_history.ToArray(), ct).ConfigureAwait(false))
        {
            sb.Append(piece);
            yield return piece;
        }
        _history.Add(new ChatMessage("assistant", sb.ToString()));
    }
}
