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
    private readonly QueryRouter _router;
    private readonly Guardrail _guardrail;
    private readonly List<IRagTool> _externalTools = new();
    private readonly LexicalIndex _lexical = new();
    private readonly SemaphoreSlim _lexicalGate = new(1, 1);
    private bool _lexicalLoaded;
    private IReranker? _reranker;

    // Profiles/guardrails cache (CRUD lives here; the store persists the full set).
    // Volatile reference swaps give lock-free, consistent reads on the hot query path.
    private volatile IReadOnlyList<ProfileInfo> _profiles = Array.Empty<ProfileInfo>();
    private volatile IReadOnlyList<GuardrailRule> _guardrails = Array.Empty<GuardrailRule>();
    private readonly SemaphoreSlim _configGate = new(1, 1);

    internal RagClient(RagOptions options, IEmbedder embedder, IVectorStore store, IChatClient answer, IChatClient classifier)
    {
        _options = options;
        _embedder = embedder;
        _store = store;
        _answer = answer;
        _classifier = new Classifier(classifier);
        _router = new QueryRouter(classifier);
        _guardrail = new Guardrail(classifier);
        // Opt-in default reranker (tier-2 reorders candidates instead of a local
        // cross-encoder). SetReranker, called after CreateAsync, always overrides this.
        if (options.EnableLlmRerank) _reranker = new LlmReranker(classifier);
        // Seed the in-memory config from options. CreateAsync later reconciles this
        // with what's persisted in the store (store wins if it already has entries).
        _profiles = options.Profiles.ToList();
        _guardrails = options.Guardrails.ToList();
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
        // Load persisted profiles/guardrails (seeding the store from options on first run).
        await client.LoadConfigAsync(ct).ConfigureAwait(false);
        // Connect any MCP servers declared in options (no-op unless RagKit.Mcp is enabled).
        await client.ConnectMcpsAsync(ct).ConfigureAwait(false);
        // The lexical index for hybrid search is built lazily on first retrieval
        // (see EnsureLexicalAsync), so startup stays cheap even on large stores.
        return client;
    }

    /// <summary>The store backing this client (for the internal data-access tools).</summary>
    public IVectorStore Store => _store;

    /// <summary>Install an optional second-stage reranker (applied after RRF fusion).</summary>
    public void SetReranker(IReranker reranker) => _reranker = reranker;

    // --- live prompt editing --------------------------------------------------
    // Forward straight to RagOptions, which SelectPrompt already re-reads on every
    // call — so these take effect on the very next Ask, no client recreation needed.
    // (Before this, the only way to edit a prompt live was for the consumer to hold
    // onto its own RagOptions instance and mutate it directly, as examples/MiniRag
    // does — these properties give any consumer, including RagKit.Dashboard, a
    // supported way to do the same without needing the original RagOptions.)

    /// <summary>System prompt (Markdown) for one-shot <see cref="AskAsync(string,string?,IReadOnlyList{string}?,string?,CancellationToken)"/>. Null → citation-aware default.</summary>
    public string? OneShotPrompt
    {
        get => _options.OneShotPrompt;
        set => _options.OneShotPrompt = value;
    }

    /// <summary>System prompt (Markdown) for <see cref="StartChat"/>. Null → falls back to <see cref="OneShotPrompt"/> or the default.</summary>
    public string? ChatPrompt
    {
        get => _options.ChatPrompt;
        set => _options.ChatPrompt = value;
    }

    /// <summary>Per-domain system prompts, read-only view — see <see cref="SetDomainPrompt"/>/<see cref="RemoveDomainPrompt"/> to edit.</summary>
    public IReadOnlyDictionary<string, string> DomainPrompts => (IReadOnlyDictionary<string, string>)_options.DomainPrompts;

    /// <summary>Set (or replace) the system prompt for a specific domain.</summary>
    public void SetDomainPrompt(string domain, string prompt) => _options.DomainPrompts[domain] = prompt;

    /// <summary>Remove a domain's prompt override, if any. Returns true if one was removed.</summary>
    public bool RemoveDomainPrompt(string domain) => _options.DomainPrompts.Remove(domain);

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

    // --- profiles & guardrails CRUD (persisted) ------------------------------

    /// <summary>Load persisted profiles/guardrails from the store; on first run, seed the
    /// store from the options' values so they survive restarts.</summary>
    internal async Task LoadConfigAsync(CancellationToken ct)
    {
        var storedProfiles = await _store.ListProfilesAsync(ct).ConfigureAwait(false);
        if (storedProfiles.Count == 0 && _profiles.Count > 0)
            await _store.SaveProfilesAsync(_profiles, ct).ConfigureAwait(false);   // seed from options
        else
            _profiles = storedProfiles;                                            // store is the source of truth

        var storedGuards = await _store.ListGuardrailsAsync(ct).ConfigureAwait(false);
        if (storedGuards.Count == 0 && _guardrails.Count > 0)
            await _store.SaveGuardrailsAsync(_guardrails, ct).ConfigureAwait(false);
        else
            _guardrails = storedGuards;
    }

    /// <summary>Create or replace a profile (matched by domain+name) and persist it.</summary>
    public async Task<ProfileInfo> DefineProfileAsync(ProfileInfo profile, CancellationToken ct = default)
    {
        await _configGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = _profiles.Where(p =>
                !(string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase) &&
                  string.Equals(p.Domain, profile.Domain, StringComparison.OrdinalIgnoreCase))).ToList();
            list.Add(profile);
            await _store.SaveProfilesAsync(list, ct).ConfigureAwait(false);
            _profiles = list;
            return profile;
        }
        finally { _configGate.Release(); }
    }

    /// <summary>Remove the profile with the given domain+name. Returns true if one was removed.</summary>
    public async Task<bool> RemoveProfileAsync(string name, string domain, CancellationToken ct = default)
    {
        await _configGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = _profiles.Where(p =>
                !(string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) &&
                  string.Equals(p.Domain, domain, StringComparison.OrdinalIgnoreCase))).ToList();
            if (list.Count == _profiles.Count) return false;
            await _store.SaveProfilesAsync(list, ct).ConfigureAwait(false);
            _profiles = list;
            return true;
        }
        finally { _configGate.Release(); }
    }

    public Task<IReadOnlyList<ProfileInfo>> ListProfilesAsync(CancellationToken ct = default)
        => Task.FromResult(_profiles);

    /// <summary>Add a guardrail rule and persist it.</summary>
    public async Task<GuardrailRule> DefineGuardrailAsync(GuardrailRule rule, CancellationToken ct = default)
    {
        await _configGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = _guardrails.Where(r => r != rule).ToList();   // records: structural equality dedupes
            list.Add(rule);
            await _store.SaveGuardrailsAsync(list, ct).ConfigureAwait(false);
            _guardrails = list;
            return rule;
        }
        finally { _configGate.Release(); }
    }

    /// <summary>Remove a guardrail rule (by value). Returns true if one was removed.</summary>
    public async Task<bool> RemoveGuardrailAsync(GuardrailRule rule, CancellationToken ct = default)
    {
        await _configGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = _guardrails.Where(r => r != rule).ToList();
            if (list.Count == _guardrails.Count) return false;
            await _store.SaveGuardrailsAsync(list, ct).ConfigureAwait(false);
            _guardrails = list;
            return true;
        }
        finally { _configGate.Release(); }
    }

    public Task<IReadOnlyList<GuardrailRule>> ListGuardrailsAsync(CancellationToken ct = default)
        => Task.FromResult(_guardrails);

    /// <summary>Connect the MCP servers declared in <see cref="RagOptions.Mcps"/> through the
    /// connector registered by RagKit.Mcp. Throws if endpoints are configured but the
    /// connector isn't enabled.</summary>
    internal async Task ConnectMcpsAsync(CancellationToken ct)
    {
        if (_options.Mcps.Count == 0) return;
        var connector = McpConnectors.Connector
            ?? throw new RagKitException(
                "Hay servidores MCP en RagOptions.Mcps pero el conector no está habilitado. " +
                "Añade el paquete RagKit.Mcp y llama a McpServers.Enable() al arrancar.");
        foreach (var entry in _options.Mcps)
            if (!string.IsNullOrWhiteSpace(entry))
                await connector(this, entry, ct).ConfigureAwait(false);
    }

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
            return new IngestResult(source, null, Array.Empty<string>(), 0, IngestOutcome.Rejected,
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
                return new IngestResult(source, domain, finalLabels, 0, IngestOutcome.Rejected,
                    Reason: $"No corresponde a ningún dominio con confianza suficiente " +
                            $"({confidence:0.00} < {_options.ClassificationThreshold:0.00}).",
                    Confidence: confidence);
        }
        else if (domain is not null &&
                 !domains.Any(x => string.Equals(x.Name, domain, StringComparison.OrdinalIgnoreCase)))
        {
            return new IngestResult(source, domain, finalLabels, 0, IngestOutcome.Rejected,
                Reason: $"El dominio '{domain}' no está definido.");
        }

        var labelArr = finalLabels.ToArray();
        var chunks = Chunker.Chunk(text);
        if (chunks.Count == 0)
            return new IngestResult(source, domain, labelArr, 0, Confidence: confidence);

        // Batch the embeddings (one round-trip / parallel inference) and the writes.
        // Every chunk in this call shares one timestamp — it's one ingestion event.
        var now = DateTime.UtcNow;
        var vectors = await _embedder.EmbedBatchAsync(chunks, ct).ConfigureAwait(false);
        var batch = new List<EmbeddedChunk>(chunks.Count);
        for (int i = 0; i < chunks.Count; i++)
            batch.Add(new EmbeddedChunk(source, chunks[i], domain, labelArr, vectors[i], now));
        await _store.AddChunksAsync(batch, ct).ConfigureAwait(false);

        // Keep the lexical index in sync only once it's been loaded; otherwise the
        // first retrieval will enumerate these chunks from the store anyway.
        if (_options.Hybrid && Volatile.Read(ref _lexicalLoaded))
            foreach (var c in batch) _lexical.Add(new StoredChunk(c.Source, c.Text, c.Domain, c.Labels, c.IngestedAtUtc));

        return new IngestResult(source, domain, labelArr, chunks.Count, Confidence: confidence);
    }

    /// <summary>Ingest a file. Text is extracted via <see cref="FileExtractors"/>
    /// (PDF/DOCX when RagKit.Extractors is enabled; plain text otherwise).</summary>
    public Task<IngestResult> IngestFileAsync(string path, string? domain = null,
        IEnumerable<string>? labels = null, CancellationToken ct = default)
        => IngestAsync(FileExtractors.Extract(path), Path.GetFileName(path), domain, labels, ct);

    private const string IngestManifestKind = "ingest-manifest";

    /// <summary>
    /// Ingest text, but skip the work entirely if it's identical to what's already
    /// stored under <paramref name="source"/>: a SHA-256 hash of <paramref name="text"/>
    /// is compared against a manifest persisted via the store's generic catalog (see
    /// <see cref="SaveCatalogEntryAsync"/>) — no domain/label classification, no
    /// embedding, no write. When the content differs (or nothing was ingested before),
    /// any previous chunks under <paramref name="source"/> are deleted (<see
    /// cref="RemoveDocumentAsync"/>) and it's ingested fresh via <see cref="IngestAsync"/>.
    /// </summary>
    public async Task<IngestResult> IngestIfChangedAsync(
        string text, string source, string? domain = null, IEnumerable<string>? labels = null, CancellationToken ct = default)
    {
        var hash = ContentHash.Compute(text);
        var manifestKey = BuildManifestKey(domain, source);
        var previousHash = await _store.GetCatalogEntryAsync(IngestManifestKind, manifestKey, ct).ConfigureAwait(false);
        if (previousHash == hash)
        {
            // The manifest hash alone isn't enough: a backend (e.g. InMemoryVectorStore)
            // may persist the catalog to disk while the chunks themselves stay in-memory
            // only, so a process restart can leave a "matching" manifest pointing at chunks
            // that no longer exist. Confirm the store still actually has content for this
            // source before trusting the hash.
            var stillPresent = await _store.ListChunksAsync(source, domain, take: 1, ct: ct).ConfigureAwait(false);
            if (stillPresent.Items.Count > 0)
                return new IngestResult(source, domain, Array.Empty<string>(), 0, IngestOutcome.Unchanged,
                    Reason: "El contenido no ha cambiado desde la última ingesta.");
        }

        if (previousHash is not null)
            await RemoveDocumentAsync(source, domain, ct).ConfigureAwait(false);

        var result = await IngestAsync(text, source, domain, labels, ct).ConfigureAwait(false);
        if (result.Outcome == IngestOutcome.Ingested)
            await _store.SaveCatalogEntryAsync(IngestManifestKind, manifestKey, hash, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Builds an unambiguous manifest key from <paramref name="domain"/> and
    /// <paramref name="source"/>: naively joining them as <c>$"{domain}:{source}"</c>
    /// lets two distinct pairs collide (e.g. <c>("a:b", "c")</c> and <c>("a", "b:c")</c>
    /// both yield <c>"a:b:c"</c>). Escaping <c>\</c> and <c>:</c> in each part before
    /// joining with an unescaped <c>:</c> makes the join injective; a control-character
    /// sentinel stands in for a null domain so it can never collide with an escaped one.
    /// </summary>
    private static string BuildManifestKey(string? domain, string source)
        => $"{(domain is null ? "\u0000" : EscapeKeyPart(domain))}:{EscapeKeyPart(source)}";

    private static string EscapeKeyPart(string s) => s.Replace("\\", "\\\\").Replace(":", "\\:");

    /// <summary>Idempotent counterpart of <see cref="IngestFileAsync"/> — see
    /// <see cref="IngestIfChangedAsync"/>.</summary>
    public Task<IngestResult> IngestFileIfChangedAsync(string path, string? domain = null,
        IEnumerable<string>? labels = null, CancellationToken ct = default)
        => IngestIfChangedAsync(FileExtractors.Extract(path), Path.GetFileName(path), domain, labels, ct);

    /// <summary>
    /// Ingest every supported file under <paramref name="path"/> (see <see
    /// cref="FileExtractors.IsSupported"/> — registered extractors plus common plain-text
    /// formats; anything else is skipped), yielding one <see cref="IngestResult"/> per file
    /// as it completes so a caller can report progress incrementally instead of waiting for
    /// the whole folder. Files that fail extraction surface as a rejected result rather than
    /// aborting the rest of the folder.
    /// </summary>
    public async IAsyncEnumerable<IngestResult> IngestFolderAsync(
        string path, string? domain = null, bool recursive = true,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var file in Directory.EnumerateFiles(path, "*", option))
        {
            ct.ThrowIfCancellationRequested();
            if (!FileExtractors.IsSupported(file)) continue;

            IngestResult result;
            try
            {
                result = await IngestFileAsync(file, domain, null, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = new IngestResult(Path.GetFileName(file), domain, Array.Empty<string>(), 0,
                    IngestOutcome.Rejected, Reason: $"Error al extraer el contenido: {ex.Message}");
            }
            yield return result;
        }
    }

    /// <summary>
    /// Remove every chunk ingested under <paramref name="source"/> (optionally scoped to
    /// a <paramref name="domain"/> — omit to remove across all domains). Returns how many
    /// chunks were removed. Also drops those entries from the in-memory hybrid (lexical)
    /// index, if it's been loaded, so stale hits don't keep surfacing.
    /// </summary>
    public async Task<int> RemoveDocumentAsync(string source, string? domain = null, CancellationToken ct = default)
    {
        var removed = await _store.DeleteBySourceAsync(source, domain, ct).ConfigureAwait(false);
        if (removed > 0 && _options.Hybrid && Volatile.Read(ref _lexicalLoaded))
            _lexical.RemoveBySource(source);
        return removed;
    }

    /// <summary>
    /// Remove an entire domain: every chunk indexed under it (<see
    /// cref="IVectorStore.DeleteByDomainAsync"/>), the domain definition itself (<see
    /// cref="IVectorStore.DeleteDomainAsync"/>), and every profile/guardrail scoped to
    /// it — a profile's <see cref="ProfileInfo.Domain"/> always names a domain, and a
    /// guardrail with <see cref="GuardrailRule.Domain"/> set (any <see
    /// cref="GuardrailRule.Profile"/>) only ever applied within it, so once the domain is
    /// gone neither means anything — worse, leaving them would let a later domain with the
    /// same name silently reactivate them. Also drops those chunks from the in-memory
    /// hybrid (lexical) index, if it's been loaded, so stale hits don't keep surfacing.
    /// Labels are untouched (they aren't domain-scoped).
    /// </summary>
    public async Task<DomainRemovalResult> RemoveDomainAsync(string name, CancellationToken ct = default)
    {
        var removed = await _store.DeleteByDomainAsync(name, ct).ConfigureAwait(false);
        var existed = await _store.DeleteDomainAsync(name, ct).ConfigureAwait(false);
        if (removed > 0 && _options.Hybrid && Volatile.Read(ref _lexicalLoaded))
            _lexical.RemoveByDomain(name);
        await RemoveDomainConfigAsync(name, ct).ConfigureAwait(false);
        return new DomainRemovalResult(existed, removed);
    }

    /// <summary>Drop every profile and guardrail scoped to <paramref name="domain"/> — the
    /// config-cleanup half of <see cref="RemoveDomainAsync"/>.</summary>
    private async Task RemoveDomainConfigAsync(string domain, CancellationToken ct)
    {
        await _configGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var profiles = _profiles.Where(p => !string.Equals(p.Domain, domain, StringComparison.OrdinalIgnoreCase)).ToList();
            if (profiles.Count != _profiles.Count)
            {
                await _store.SaveProfilesAsync(profiles, ct).ConfigureAwait(false);
                _profiles = profiles;
            }

            var guardrails = _guardrails.Where(r => !string.Equals(r.Domain, domain, StringComparison.OrdinalIgnoreCase)).ToList();
            if (guardrails.Count != _guardrails.Count)
            {
                await _store.SaveGuardrailsAsync(guardrails, ct).ConfigureAwait(false);
                _guardrails = guardrails;
            }
        }
        finally { _configGate.Release(); }
    }

    /// <summary>List ingested documents (one entry per distinct source), optionally
    /// scoped to a domain — the aggregate view over the individual chunks.</summary>
    public Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(string? domain = null, CancellationToken ct = default)
        => _store.ListDocumentsAsync(domain, ct);

    /// <summary>
    /// One page of the chunks under <paramref name="source"/> (optionally scoped to a
    /// <paramref name="domain"/>). Pass the previous call's <see
    /// cref="ChunkPage.NextCursor"/> as <paramref name="cursor"/> to fetch the next
    /// page; start with <c>cursor: null</c> and keep going until it comes back null.
    /// </summary>
    public Task<ChunkPage> ListChunksAsync(string source, string? domain = null, int take = 100, string? cursor = null, CancellationToken ct = default)
        => _store.ListChunksAsync(source, domain, take, cursor, ct);

    // --- generic catalog: consumer-owned key/value metadata, persisted alongside the vectors ---

    /// <summary>Read a catalog entry previously saved with <see cref="SaveCatalogEntryAsync"/>, or
    /// null if absent (or the store doesn't support the catalog — see <see cref="IVectorStore"/>).</summary>
    public Task<string?> GetCatalogEntryAsync(string kind, string key, CancellationToken ct = default)
        => _store.GetCatalogEntryAsync(kind, key, ct);

    /// <summary>Write (create or replace) a catalog entry: an arbitrary JSON blob keyed by
    /// (<paramref name="kind"/>, <paramref name="key"/>) — RagKit never interprets either.</summary>
    public Task SaveCatalogEntryAsync(string kind, string key, string json, CancellationToken ct = default)
        => _store.SaveCatalogEntryAsync(kind, key, json, ct);

    /// <summary>Delete a catalog entry, if present.</summary>
    public Task DeleteCatalogEntryAsync(string kind, string key, CancellationToken ct = default)
        => _store.DeleteCatalogEntryAsync(kind, key, ct);

    // --- ask -----------------------------------------------------------------

    /// <summary>
    /// One-shot RAG: route (if no explicit domain/profile), guardrail the query,
    /// retrieve, answer with the resolved prompt, guardrail the answer. Works with
    /// any model. Pass <paramref name="domain"/> and/or <paramref name="profile"/>
    /// to skip routing; leave them null to let the tier-2 model route.
    /// </summary>
    public async Task<RagAnswer> AskAsync(
        string question, string? domain = null, IReadOnlyList<string>? labels = null,
        string? profile = null, CancellationToken ct = default)
    {
        var route = await ResolveRouteAsync(question, domain, profile, ct).ConfigureAwait(false);
        var primary = route.Profiles.Count > 0 ? route.Profiles[0] : null;

        var guard = await GuardInputAsync(question, route.Domain, primary, ct).ConfigureAwait(false);
        if (!guard.Allowed)
            return new RagAnswer(_options.GuardrailRejectionMessage, Array.Empty<Citation>());

        var hits = await RetrieveAsync(question, route.Domain, MergeLabels(labels, route.Labels), ct).ConfigureAwait(false);
        var messages = new[]
        {
            new ChatMessage("system", SelectPrompt(route.Domain, primary, _options.OneShotPrompt ?? Prompt.DefaultSystem)),
            new ChatMessage("user", Prompt.BuildUser(question, hits)),
        };
        var answer = await _answer.CompleteAsync(messages, ct).ConfigureAwait(false);

        var outGuard = await GuardOutputAsync(answer, route.Domain, primary, ct).ConfigureAwait(false);
        if (!outGuard.Allowed)
            return new RagAnswer(_options.GuardrailRejectionMessage, ToCitations(hits));

        return new RagAnswer(answer, ToCitations(hits));
    }

    /// <summary>
    /// One-shot RAG, streamed: routes and input-guards as <see cref="AskAsync"/>, then
    /// streams the grounded answer token-by-token. <see cref="RagStream.Citations"/>
    /// are ready immediately. If an OUTPUT guardrail applies to the scope, the answer
    /// is buffered, validated and then emitted as a single chunk (tokens can't be
    /// un-sent) — so live streaming only degrades when you've configured output rules.
    /// </summary>
    public async Task<RagStream> AskStreamAsync(
        string question, string? domain = null, IReadOnlyList<string>? labels = null,
        string? profile = null, CancellationToken ct = default)
    {
        var route = await ResolveRouteAsync(question, domain, profile, ct).ConfigureAwait(false);
        var primary = route.Profiles.Count > 0 ? route.Profiles[0] : null;

        var guard = await GuardInputAsync(question, route.Domain, primary, ct).ConfigureAwait(false);
        if (!guard.Allowed)
            return new RagStream(Array.Empty<Citation>(), One(_options.GuardrailRejectionMessage));

        var hits = await RetrieveAsync(question, route.Domain, MergeLabels(labels, route.Labels), ct).ConfigureAwait(false);
        var messages = new[]
        {
            new ChatMessage("system", SelectPrompt(route.Domain, primary, _options.OneShotPrompt ?? Prompt.DefaultSystem)),
            new ChatMessage("user", Prompt.BuildUser(question, hits)),
        };

        // If an output guardrail applies, we can't stream live (tokens can't be un-sent):
        // buffer the full answer, validate it, then emit it as one chunk (or reject).
        if (HasOutputGuardrail(route.Domain, primary))
        {
            var full = await _answer.CompleteAsync(messages, ct).ConfigureAwait(false);
            var og = await GuardOutputAsync(full, route.Domain, primary, ct).ConfigureAwait(false);
            return og.Allowed
                ? new RagStream(ToCitations(hits), One(full))
                : new RagStream(ToCitations(hits), One(_options.GuardrailRejectionMessage));
        }

        return new RagStream(ToCitations(hits), _answer.StreamAsync(messages, ct));
    }

    /// <summary>
    /// Multi-turn RAG as a pure function: <paramref name="priorHistory"/> is the
    /// conversation so far (as the caller persisted it — no session object, no shared
    /// mutable state, safe to call concurrently for different conversations). Routes,
    /// input-guards, retrieves and answers exactly like <see cref="AskAsync(string,string?,IReadOnlyList{string}?,string?,CancellationToken)"/>,
    /// but grounds the answer on <paramref name="priorHistory"/> plus this turn instead
    /// of starting fresh. The returned history for the *next* turn is
    /// <paramref name="priorHistory"/> + this question + this answer — the caller
    /// appends and persists it; RagKit keeps none of it.
    /// </summary>
    public async Task<RagAnswer> AskAsync(
        string question, IReadOnlyList<ChatMessage> priorHistory,
        string? domain = null, string? profile = null, CancellationToken ct = default)
    {
        var route = await ResolveRouteAsync(question, domain, profile, ct).ConfigureAwait(false);
        var primary = route.Profiles.Count > 0 ? route.Profiles[0] : null;

        var guard = await GuardInputAsync(question, route.Domain, primary, ct).ConfigureAwait(false);
        if (!guard.Allowed)
            return new RagAnswer(_options.GuardrailRejectionMessage, Array.Empty<Citation>());

        var hits = await RetrieveAsync(question, route.Domain, route.Labels, ct).ConfigureAwait(false);
        var convo = BuildConversation(route.Domain, primary, priorHistory, question, hits);
        var answer = await _answer.CompleteAsync(convo, ct).ConfigureAwait(false);

        var outGuard = await GuardOutputAsync(answer, route.Domain, primary, ct).ConfigureAwait(false);
        if (!outGuard.Allowed)
            return new RagAnswer(_options.GuardrailRejectionMessage, ToCitations(hits));

        return new RagAnswer(answer, ToCitations(hits));
    }

    /// <summary>Streamed counterpart of <see cref="AskAsync(string,IReadOnlyList{ChatMessage},string?,string?,CancellationToken)"/> —
    /// same explicit-history contract, buffer-then-emit under an output guardrail exactly
    /// like <see cref="AskStreamAsync(string,string?,IReadOnlyList{string}?,string?,CancellationToken)"/>.</summary>
    public async Task<RagStream> AskStreamAsync(
        string question, IReadOnlyList<ChatMessage> priorHistory,
        string? domain = null, string? profile = null, CancellationToken ct = default)
    {
        var route = await ResolveRouteAsync(question, domain, profile, ct).ConfigureAwait(false);
        var primary = route.Profiles.Count > 0 ? route.Profiles[0] : null;

        var guard = await GuardInputAsync(question, route.Domain, primary, ct).ConfigureAwait(false);
        if (!guard.Allowed)
            return new RagStream(Array.Empty<Citation>(), One(_options.GuardrailRejectionMessage));

        var hits = await RetrieveAsync(question, route.Domain, route.Labels, ct).ConfigureAwait(false);
        var convo = BuildConversation(route.Domain, primary, priorHistory, question, hits);

        if (HasOutputGuardrail(route.Domain, primary))
        {
            var full = await _answer.CompleteAsync(convo, ct).ConfigureAwait(false);
            var og = await GuardOutputAsync(full, route.Domain, primary, ct).ConfigureAwait(false);
            return og.Allowed
                ? new RagStream(ToCitations(hits), One(full))
                : new RagStream(ToCitations(hits), One(_options.GuardrailRejectionMessage));
        }

        return new RagStream(ToCitations(hits), _answer.StreamAsync(convo, ct));
    }

    /// <summary>Assemble system prompt + prior turns + this grounded question, for the
    /// explicit-history <c>AskAsync</c>/<c>AskStreamAsync</c> overloads.</summary>
    private List<ChatMessage> BuildConversation(
        string? domain, string? profile, IReadOnlyList<ChatMessage> priorHistory, string question, IReadOnlyList<StoredHit> hits)
    {
        var systemPrompt = SelectPrompt(domain, profile, _options.ChatPrompt ?? _options.OneShotPrompt ?? Prompt.DefaultSystem);
        var convo = new List<ChatMessage>(priorHistory.Count + 2) { new("system", systemPrompt) };
        convo.AddRange(priorHistory);
        convo.Add(new ChatMessage("user", Prompt.BuildUser(question, hits)));
        return convo;
    }

    /// <summary>
    /// Route a query (pick domain + profile[s]) with the tier-2 model, without
    /// answering. Lets an app show or override the routing before calling
    /// <see cref="AskAsync"/> with an explicit domain/profile.
    /// </summary>
    public async Task<RouteDecision> RouteQueryAsync(string question, CancellationToken ct = default)
    {
        var domains = await _store.ListDomainsAsync(ct).ConfigureAwait(false);
        if (domains.Count == 0)
            return new RouteDecision(null, Array.Empty<string>(), Array.Empty<string>(), 0.0);
        return await _router.RouteAsync(question, domains, _profiles, _options.MultiProfile, ct).ConfigureAwait(false);
    }

    /// <summary>Start a multi-turn chat (optionally scoped to a domain/profile), grounded per
    /// turn. With neither given, the first turn is routed and the choice is reused for the session.</summary>
    public ChatSession StartChat(string? domain = null, string? profile = null)
        => new(this, _options.ChatPrompt ?? _options.OneShotPrompt ?? Prompt.DefaultSystem, domain, profile);

    /// <summary>Register an external tool (e.g. an MCP server adapter) for the agent loop.</summary>
    public void RegisterTool(IRagTool tool) => _externalTools.Add(tool);

    /// <summary>
    /// Agentic answer: the model decides when to search the knowledge base, list
    /// or create domains/labels, ingest, or call external (MCP) tools — looping
    /// until it answers. Requires a tool-capable model; otherwise it transparently
    /// falls back to one-shot <see cref="AskAsync"/>. <paramref name="tools"/> defaults
    /// to <see cref="AgentToolScope.All"/> (the pre-scoping behavior); pass
    /// <see cref="AgentToolScope.SearchOnly"/> for public/unauthenticated callers — see
    /// <see cref="AgentToolScope"/>.
    /// </summary>
    public Task<RagAnswer> AskAgentAsync(
        string question, string? domain = null, int maxSteps = 5,
        AgentToolScope tools = AgentToolScope.All, CancellationToken ct = default)
        => AskAgentCoreAsync(question, Array.Empty<ChatMessage>(), domain, null, maxSteps, tools, ct);

    /// <summary>
    /// Agentic answer as a pure function over explicit history, mirroring
    /// <see cref="AskAsync(string,IReadOnlyList{ChatMessage},string?,string?,CancellationToken)"/>:
    /// <paramref name="priorHistory"/> is the conversation so far (caller-persisted, no
    /// hidden session state) and <paramref name="profile"/> pins the lens the same way the
    /// non-agentic overload does, instead of re-routing from a blank profile every turn.
    /// Same tool loop, scoping and guardrails as
    /// <see cref="AskAgentAsync(string,string?,int,AgentToolScope,CancellationToken)"/>.
    /// </summary>
    public Task<RagAnswer> AskAgentAsync(
        string question, IReadOnlyList<ChatMessage> priorHistory,
        string? domain = null, string? profile = null, int maxSteps = 5,
        AgentToolScope tools = AgentToolScope.All, CancellationToken ct = default)
        => AskAgentCoreAsync(question, priorHistory, domain, profile, maxSteps, tools, ct);

    private async Task<RagAnswer> AskAgentCoreAsync(
        string question, IReadOnlyList<ChatMessage> priorHistory, string? domain, string? profile,
        int maxSteps, AgentToolScope toolScope, CancellationToken ct)
    {
        if (!_answer.SupportsTools)
            return priorHistory.Count > 0
                ? await AskAsync(question, priorHistory, domain, profile, ct).ConfigureAwait(false)
                : await AskAsync(question, domain, null, profile, ct).ConfigureAwait(false);

        // Same front door as AskAsync: route (when not pinned) and guardrail the query
        // BEFORE any tool runs, so the agentic path can't bypass the guardrail.
        var route = await ResolveRouteAsync(question, domain, profile, ct).ConfigureAwait(false);
        var primary = route.Profiles.Count > 0 ? route.Profiles[0] : null;

        var guard = await GuardInputAsync(question, route.Domain, primary, ct).ConfigureAwait(false);
        if (!guard.Allowed)
            return new RagAnswer(_options.GuardrailRejectionMessage, Array.Empty<Citation>());

        var search = new SearchTool(this, route.Domain);
        var tools = BuildAgentTools(search, route.Domain, toolScope);
        var specs = tools.Select(t => new ToolSpec(t.Name, t.Description, t.ParametersSchema)).ToList();
        var byName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

        var msgs = new List<AgentMessage>
        {
            new("system", SelectPrompt(route.Domain, primary, _options.OneShotPrompt ?? Prompt.AgentSystem)),
        };
        foreach (var h in priorHistory) msgs.Add(new AgentMessage(h.Role, h.Content));
        msgs.Add(new AgentMessage("user", question));

        for (int step = 0; step < maxSteps; step++)
        {
            var turn = await _answer.NextAsync(msgs, specs, ct).ConfigureAwait(false);
            if (turn.ToolCalls.Count == 0)
                return await GuardedAgentAnswer(turn.Content ?? "", route.Domain, primary, search, ct).ConfigureAwait(false);

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
        return await GuardedAgentAnswer(final, route.Domain, primary, search, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Streamed counterpart of <see cref="AskAgentAsync(string,string?,int,AgentToolScope,CancellationToken)"/>:
    /// same tool loop, scoping and guardrails, but yields an <see cref="AgentStream"/>
    /// mixing tool-calling activity (<see cref="AgentStreamEventKind.ToolCallStarted"/>/
    /// <see cref="AgentStreamEventKind.ToolCallFinished"/>) with the final answer's
    /// tokens as they arrive. Falls back to <see
    /// cref="AskStreamAsync(string,string?,IReadOnlyList{string}?,string?,CancellationToken)"/>
    /// (adapted into the same event shape, with no tool-calling events) when the
    /// model doesn't support tools.
    /// </summary>
    public Task<AgentStream> AskAgentStreamAsync(
        string question, string? domain = null, int maxSteps = 5,
        AgentToolScope tools = AgentToolScope.All, CancellationToken ct = default)
        => AskAgentStreamCoreAsync(question, Array.Empty<ChatMessage>(), domain, null, maxSteps, tools, ct);

    /// <summary>
    /// Streamed agentic answer over explicit history, mirroring <see
    /// cref="AskAgentAsync(string,IReadOnlyList{ChatMessage},string?,string?,int,AgentToolScope,CancellationToken)"/>.
    /// Same tool loop, scoping and guardrails as
    /// <see cref="AskAgentStreamAsync(string,string?,int,AgentToolScope,CancellationToken)"/>.
    /// </summary>
    public Task<AgentStream> AskAgentStreamAsync(
        string question, IReadOnlyList<ChatMessage> priorHistory,
        string? domain = null, string? profile = null, int maxSteps = 5,
        AgentToolScope tools = AgentToolScope.All, CancellationToken ct = default)
        => AskAgentStreamCoreAsync(question, priorHistory, domain, profile, maxSteps, tools, ct);

    private async Task<AgentStream> AskAgentStreamCoreAsync(
        string question, IReadOnlyList<ChatMessage> priorHistory, string? domain, string? profile,
        int maxSteps, AgentToolScope toolScope, CancellationToken ct)
    {
        if (!_answer.SupportsTools)
        {
            var plain = priorHistory.Count > 0
                ? await AskStreamAsync(question, priorHistory, domain, profile, ct).ConfigureAwait(false)
                : await AskStreamAsync(question, domain, null, profile, ct).ConfigureAwait(false);
            return new AgentStream(AdaptRagStreamAsync(plain, ct));
        }

        // Same front door as AskAgentAsync: route (when not pinned) and guardrail the
        // query BEFORE any tool runs, so the agentic path can't bypass the guardrail.
        var route = await ResolveRouteAsync(question, domain, profile, ct).ConfigureAwait(false);
        var primary = route.Profiles.Count > 0 ? route.Profiles[0] : null;

        var guard = await GuardInputAsync(question, route.Domain, primary, ct).ConfigureAwait(false);
        if (!guard.Allowed)
            return new AgentStream(RejectedAgentStreamAsync(_options.GuardrailRejectionMessage));

        var search = new SearchTool(this, route.Domain);
        var tools = BuildAgentTools(search, route.Domain, toolScope);
        var specs = tools.Select(t => new ToolSpec(t.Name, t.Description, t.ParametersSchema)).ToList();
        var byName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

        var msgs = new List<AgentMessage>
        {
            new("system", SelectPrompt(route.Domain, primary, _options.OneShotPrompt ?? Prompt.AgentSystem)),
        };
        foreach (var h in priorHistory) msgs.Add(new AgentMessage(h.Role, h.Content));
        msgs.Add(new AgentMessage("user", question));

        return new AgentStream(RunAgentStreamAsync(msgs, specs, byName, maxSteps, route.Domain, primary, search, ct));
    }

    private static async IAsyncEnumerable<AgentStreamEvent> RejectedAgentStreamAsync(string message)
    {
        yield return new AgentStreamEvent(AgentStreamEventKind.Citations, Citations: Array.Empty<Citation>());
        yield return new AgentStreamEvent(AgentStreamEventKind.Token, Token: message);
    }

    /// <summary>Adapts a plain (non-agentic) <see cref="RagStream"/> into the agentic
    /// event shape, for callers/models that don't support tool-calling.</summary>
    private static async IAsyncEnumerable<AgentStreamEvent> AdaptRagStreamAsync(
        RagStream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new AgentStreamEvent(AgentStreamEventKind.Citations, Citations: stream.Citations);
        await foreach (var token in stream.Tokens.WithCancellation(ct))
            yield return new AgentStreamEvent(AgentStreamEventKind.Token, Token: token);
    }

    /// <summary>The streamed agent loop itself: same shape as <see cref="AskAgentCoreAsync"/>'s
    /// step loop, but each turn is read via <see cref="IChatClient.NextStreamAsync"/> so a
    /// content-only (final) turn can be relayed live instead of waited-for-then-returned.
    /// An applicable output guardrail buffers that final turn instead (can't validate
    /// already-emitted tokens) — same "buffer-then-emit" degradation <see
    /// cref="AskStreamAsync(string,string?,IReadOnlyList{string}?,string?,CancellationToken)"/>
    /// already uses.</summary>
    private async IAsyncEnumerable<AgentStreamEvent> RunAgentStreamAsync(
        List<AgentMessage> msgs, List<ToolSpec> specs, Dictionary<string, IRagTool> byName, int maxSteps,
        string? domain, string? profile, SearchTool search, [EnumeratorCancellation] CancellationToken ct)
    {
        var bufferOutput = HasOutputGuardrail(domain, profile);

        for (int step = 0; step < maxSteps; step++)
        {
            List<ToolCall>? toolCallsThisTurn = null;
            var contentSoFar = new StringBuilder();
            var citationsEmitted = false;

            await foreach (var delta in _answer.NextStreamAsync(msgs, specs, ct).ConfigureAwait(false))
            {
                if (delta.Kind == AgentDeltaKind.ToolCallStarted)
                {
                    yield return new AgentStreamEvent(AgentStreamEventKind.ToolCallStarted, ToolName: delta.ToolName);
                }
                else if (delta.Kind == AgentDeltaKind.ContentPiece)
                {
                    if (!citationsEmitted)
                    {
                        citationsEmitted = true;
                        if (!bufferOutput)
                            yield return new AgentStreamEvent(AgentStreamEventKind.Citations, Citations: search.Citations.ToList());
                    }
                    contentSoFar.Append(delta.Content);
                    if (!bufferOutput)
                        yield return new AgentStreamEvent(AgentStreamEventKind.Token, Token: delta.Content);
                }
                else if (delta.Kind == AgentDeltaKind.ToolCallsReady)
                {
                    toolCallsThisTurn = delta.ToolCalls?.ToList();
                }
            }

            if (toolCallsThisTurn is { Count: > 0 })
            {
                msgs.Add(new AgentMessage("assistant", null, ToolCalls: toolCallsThisTurn));
                foreach (var call in toolCallsThisTurn)
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
                    yield return new AgentStreamEvent(AgentStreamEventKind.ToolCallFinished,
                        ToolName: call.Name, ToolResultSummary: Snippet(result));
                }
                continue;
            }

            // Content-only (or truly empty) turn: this was the final answer.
            if (bufferOutput)
            {
                var finalText = contentSoFar.ToString();
                var og = await GuardOutputAsync(finalText, domain, profile, ct).ConfigureAwait(false);
                yield return new AgentStreamEvent(AgentStreamEventKind.Citations, Citations: search.Citations.ToList());
                yield return new AgentStreamEvent(AgentStreamEventKind.Token,
                    Token: og.Allowed ? finalText : _options.GuardrailRejectionMessage);
            }
            else if (!citationsEmitted)
            {
                // Truly empty turn (no tool calls, zero content pieces) — still surface
                // whatever citations were gathered, for parity with the non-streamed loop
                // (GuardedAgentAnswer always returns a RagAnswer even when Content is empty).
                yield return new AgentStreamEvent(AgentStreamEventKind.Citations, Citations: search.Citations.ToList());
            }
            yield break;
        }

        // Out of steps: force a final answer without tools (same fallback as the
        // non-streamed loop), emitted as a single buffered Token.
        var final = await _answer.CompleteAsync(
            msgs.Where(m => m.Role is "system" or "user" or "assistant")
                .Select(m => new ChatMessage(m.Role, m.Content ?? "")).ToList(), ct).ConfigureAwait(false);
        var outGuard = await GuardOutputAsync(final, domain, profile, ct).ConfigureAwait(false);
        yield return new AgentStreamEvent(AgentStreamEventKind.Citations, Citations: search.Citations.ToList());
        yield return new AgentStreamEvent(AgentStreamEventKind.Token,
            Token: outGuard.Allowed ? final : _options.GuardrailRejectionMessage);
    }

    /// <summary>Build the tool list for one agent-loop call, scoped by <paramref name="scope"/>.
    /// <c>search</c> is always included — <see cref="AgentToolScope.SearchOnly"/> can't be
    /// turned off, it's the safe floor for untrusted callers.</summary>
    private List<IRagTool> BuildAgentTools(SearchTool search, string? domain, AgentToolScope scope)
    {
        var tools = new List<IRagTool> { search };
        if (scope.HasFlag(AgentToolScope.Classification))
        {
            tools.Add(new ListDomainsTool(this));
            tools.Add(new ListLabelsTool(this));
        }
        if (scope.HasFlag(AgentToolScope.Mutation))
        {
            tools.Add(new CreateDomainTool(this));
            tools.Add(new CreateLabelTool(this));
            tools.Add(new IngestTool(this, domain));
            tools.Add(new CreateProfileTool(this));
            tools.Add(new CreateGuardrailTool(this));
        }
        if (scope.HasFlag(AgentToolScope.External))
            tools.AddRange(_externalTools);
        return tools;
    }

    /// <summary>Apply the output guardrail to an agentic answer before returning it.</summary>
    private async Task<RagAnswer> GuardedAgentAnswer(string answer, string? domain, string? profile, SearchTool search, CancellationToken ct)
    {
        var og = await GuardOutputAsync(answer, domain, profile, ct).ConfigureAwait(false);
        return og.Allowed
            ? new RagAnswer(answer, search.Citations)
            : new RagAnswer(_options.GuardrailRejectionMessage, search.Citations);
    }

    // --- routing / prompt / guardrail (shared with ChatSession) --------------

    /// <summary>The resolved scope of a query: where to search and which lens to apply.</summary>
    internal sealed record Resolved(string? Domain, IReadOnlyList<string> Profiles, IReadOnlyList<string> Labels);

    internal string GuardrailRejectionMessage => _options.GuardrailRejectionMessage;

    /// <summary>
    /// Resolve domain + profile[s] for a query. Explicit domain/profile skip routing.
    /// Otherwise, if routing is on and there's something to route (more than one
    /// domain, or any profiles), the tier-2 model picks; a low-confidence result
    /// degrades to the safe default (single domain or none, no profile).
    /// </summary>
    internal async Task<Resolved> ResolveRouteAsync(string question, string? domain, string? profile, CancellationToken ct)
    {
        var domains = await _store.ListDomainsAsync(ct).ConfigureAwait(false);

        if (domain is null && profile is null && _options.EnableQueryRouting && domains.Count > 0
            && (domains.Count > 1 || _profiles.Count > 0))
        {
            var decision = await _router.RouteAsync(question, domains, _profiles, _options.MultiProfile, ct).ConfigureAwait(false);
            if (decision.Confidence >= _options.RoutingThreshold)
            {
                var routed = decision.Domain ?? (domains.Count == 1 ? domains[0].Name : null);
                return new Resolved(routed, decision.Profiles, decision.Labels);
            }
            // Low confidence → fall through to the safe defaults below.
        }

        var resolvedDomain = domain ?? (domains.Count == 1 ? domains[0].Name : null);
        var profiles = profile is not null ? new[] { profile } : Array.Empty<string>();
        return new Resolved(resolvedDomain, profiles, ProfileLabels(resolvedDomain, profiles));
    }

    /// <summary>Union of the labels mapped by the given profiles within a domain.</summary>
    private IReadOnlyList<string> ProfileLabels(string? domain, IReadOnlyList<string> profiles)
    {
        if (domain is null || profiles.Count == 0) return Array.Empty<string>();
        var labels = new List<string>();
        foreach (var name in profiles)
        {
            var p = _profiles.FirstOrDefault(x =>
                string.Equals(x.Domain, domain, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (p?.Labels is null) continue;
            foreach (var l in p.Labels) if (!labels.Contains(l)) labels.Add(l);
        }
        return labels;
    }

    /// <summary>Prompt-resolution chain: (domain,profile) → per-domain → global fallback.</summary>
    internal string SelectPrompt(string? domain, string? profile, string globalFallback)
    {
        if (domain is not null && profile is not null)
        {
            var p = _profiles.FirstOrDefault(x =>
                string.Equals(x.Domain, domain, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Name, profile, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(p?.Prompt)) return p!.Prompt!;
        }
        if (domain is not null && _options.DomainPrompts.TryGetValue(domain, out var dp) && !string.IsNullOrWhiteSpace(dp))
            return dp;
        return globalFallback;
    }

    private IReadOnlyList<GuardrailRule> ApplicableRules(string? domain, string? profile, GuardrailStage stage)
        => _guardrails.Where(r => r.Stage == stage
            && (r.Domain is null || string.Equals(r.Domain, domain, StringComparison.OrdinalIgnoreCase))
            && (r.Profile is null || string.Equals(r.Profile, profile, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    /// <summary>Whether an output guardrail would actually run for this scope (used to decide
    /// if a streamed answer must be buffered before emitting).</summary>
    internal bool HasOutputGuardrail(string? domain, string? profile)
        => _options.EnableOutputGuardrail && ApplicableRules(domain, profile, GuardrailStage.Output).Count > 0;

    internal Task<GuardDecision> GuardInputAsync(string query, string? domain, string? profile, CancellationToken ct)
        => _options.EnableInputGuardrail
            ? _guardrail.CheckInputAsync(query, ApplicableRules(domain, profile, GuardrailStage.Input),
                _options.MaxQueryLength, _options.GuardrailPiiCheck, ct)
            : Task.FromResult(new GuardDecision(true, null));

    internal Task<GuardDecision> GuardOutputAsync(string answer, string? domain, string? profile, CancellationToken ct)
        => _options.EnableOutputGuardrail
            ? _guardrail.CheckOutputAsync(answer, ApplicableRules(domain, profile, GuardrailStage.Output), ct)
            : Task.FromResult(new GuardDecision(true, null));

    /// <summary>Combine explicit labels with profile-mapped labels (null when neither applies).</summary>
    private static IReadOnlyList<string>? MergeLabels(IReadOnlyList<string>? explicitLabels, IReadOnlyList<string> profileLabels)
    {
        if ((explicitLabels is null || explicitLabels.Count == 0) && profileLabels.Count == 0) return null;
        var set = new List<string>();
        if (explicitLabels is not null) foreach (var l in explicitLabels) if (!set.Contains(l)) set.Add(l);
        foreach (var l in profileLabels) if (!set.Contains(l)) set.Add(l);
        return set.Count > 0 ? set : null;
    }

    /// <summary>Yield a single string as an async stream (used for guardrail rejections).</summary>
    internal static async IAsyncEnumerable<string> One(string s)
    {
        yield return s;
        await Task.CompletedTask;
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
            rep.TryAdd(key, new StoredHit(c.Source, c.Text, c.Domain, c.Labels, 0, c.Id));
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

/// <summary>
/// A conversational session that augments each user turn with retrieval. Domain
/// and profile are resolved once (on the first turn, routed if not given) and
/// reused — so routing latency is paid only once per chat. The input guardrail
/// runs on every turn; the output guardrail on non-streamed turns.
/// </summary>
public sealed class ChatSession
{
    private readonly RagClient _rag;
    private readonly string _globalPrompt;
    private readonly string? _domainParam;
    private readonly string? _profileParam;
    private readonly List<ChatMessage> _history = new();

    private bool _resolved;
    private string? _domain;
    private string? _profile;
    private IReadOnlyList<string>? _labels;

    internal ChatSession(RagClient rag, string globalPrompt, string? domain, string? profile)
    {
        _rag = rag;
        _globalPrompt = globalPrompt;
        _domainParam = domain;
        _profileParam = profile;
    }

    /// <summary>The domain this session resolved to (available after the first turn).</summary>
    public string? Domain => _domain;

    /// <summary>The profile this session resolved to (available after the first turn).</summary>
    public string? Profile => _profile;

    /// <summary>On the first turn, route (if needed), pick the system prompt and seed history.</summary>
    private async Task EnsureResolvedAsync(string firstMessage, CancellationToken ct)
    {
        if (_resolved) return;
        var route = await _rag.ResolveRouteAsync(firstMessage, _domainParam, _profileParam, ct).ConfigureAwait(false);
        _domain = route.Domain;
        _profile = route.Profiles.Count > 0 ? route.Profiles[0] : null;
        _labels = route.Labels.Count > 0 ? route.Labels : null;
        _history.Add(new ChatMessage("system", _rag.SelectPrompt(_domain, _profile, _globalPrompt)));
        _resolved = true;
    }

    public async Task<RagAnswer> AskAsync(string message, CancellationToken ct = default)
    {
        await EnsureResolvedAsync(message, ct).ConfigureAwait(false);

        var guard = await _rag.GuardInputAsync(message, _domain, _profile, ct).ConfigureAwait(false);
        if (!guard.Allowed)
            return new RagAnswer(_rag.GuardrailRejectionMessage, Array.Empty<Citation>());

        var hits = await _rag.RetrieveAsync(message, _domain, _labels, ct).ConfigureAwait(false);
        var userMsg = new ChatMessage("user", Internal.Prompt.BuildUser(message, hits));
        var convo = new List<ChatMessage>(_history) { userMsg };
        var answer = await _rag.CompleteAnswerAsync(convo, ct).ConfigureAwait(false);

        var outGuard = await _rag.GuardOutputAsync(answer, _domain, _profile, ct).ConfigureAwait(false);
        if (!outGuard.Allowed)
            return new RagAnswer(_rag.GuardrailRejectionMessage, RagClient.ToCitations(hits));

        // Only commit a turn that passed both guardrails to the history.
        _history.Add(userMsg);
        _history.Add(new ChatMessage("assistant", answer));
        return new RagAnswer(answer, RagClient.ToCitations(hits));
    }

    /// <summary>Streamed turn: grounds on retrieval, streams the answer, and records
    /// the full assistant turn in history once the stream completes.</summary>
    public async Task<RagStream> AskStreamAsync(string message, CancellationToken ct = default)
    {
        await EnsureResolvedAsync(message, ct).ConfigureAwait(false);

        var guard = await _rag.GuardInputAsync(message, _domain, _profile, ct).ConfigureAwait(false);
        if (!guard.Allowed)
            return new RagStream(Array.Empty<Citation>(), RagClient.One(_rag.GuardrailRejectionMessage));

        var hits = await _rag.RetrieveAsync(message, _domain, _labels, ct).ConfigureAwait(false);
        var userMsg = new ChatMessage("user", Internal.Prompt.BuildUser(message, hits));

        // If an output guardrail applies, buffer-then-emit (can't un-send streamed tokens).
        if (_rag.HasOutputGuardrail(_domain, _profile))
        {
            var convo = new List<ChatMessage>(_history) { userMsg };
            var full = await _rag.CompleteAnswerAsync(convo, ct).ConfigureAwait(false);
            var og = await _rag.GuardOutputAsync(full, _domain, _profile, ct).ConfigureAwait(false);
            if (!og.Allowed)
                return new RagStream(RagClient.ToCitations(hits), RagClient.One(_rag.GuardrailRejectionMessage));
            _history.Add(userMsg);
            _history.Add(new ChatMessage("assistant", full));
            return new RagStream(RagClient.ToCitations(hits), RagClient.One(full));
        }

        return new RagStream(RagClient.ToCitations(hits), StreamAndRecord(userMsg, ct));
    }

    private async IAsyncEnumerable<string> StreamAndRecord(ChatMessage userMsg, [EnumeratorCancellation] CancellationToken ct)
    {
        var convo = new List<ChatMessage>(_history) { userMsg };
        var sb = new StringBuilder();
        await foreach (var piece in _rag.StreamAnswerAsync(convo.ToArray(), ct).ConfigureAwait(false))
        {
            sb.Append(piece);
            yield return piece;
        }
        _history.Add(userMsg);
        _history.Add(new ChatMessage("assistant", sb.ToString()));
    }
}
