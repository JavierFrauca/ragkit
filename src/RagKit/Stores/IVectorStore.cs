namespace RagKit;

/// <summary>Which vector-store backend to use.</summary>
public enum VectorStoreKind
{
    /// <summary>In-process, in-memory (with on-disk metadata). Zero setup; the default.</summary>
    InMemory,
    /// <summary>SQL Server with the VECTOR type (connector — see factory).</summary>
    SqlServer,
    /// <summary>Qdrant (connector — see factory).</summary>
    Qdrant,
    /// <summary>PostgreSQL + pgvector (connector — see factory).</summary>
    Postgres,
}

/// <summary>A retrieved chunk with its score and provenance.</summary>
public sealed record StoredHit(string Source, string Text, string? Domain, IReadOnlyList<string> Labels, double Score);

/// <summary>A stored chunk (no score) — used to rebuild the lexical index for hybrid search,
/// and to aggregate the document inventory (<see cref="IVectorStore.ListDocumentsAsync"/>).</summary>
public sealed record StoredChunk(string Source, string Text, string? Domain, IReadOnlyList<string> Labels, DateTime IngestedAtUtc = default);

/// <summary>A chunk plus its vector, ready to be indexed — the unit of batch ingestion.</summary>
public sealed record EmbeddedChunk(string Source, string Text, string? Domain, IReadOnlyList<string> Labels, float[] Vector, DateTime IngestedAtUtc = default);

/// <summary>
/// The storage contract: identical across backends so a consumer swaps SQL
/// Server / Qdrant / Postgres / in-memory by changing one enum. It owns the
/// vectors, the domain/label structure and the embedding metadata guard.
/// </summary>
public interface IVectorStore
{
    /// <summary>
    /// Open/create the store for an embedding identified by <paramref name="modelId"/>
    /// and <paramref name="dimension"/>. If the store already holds collections
    /// created with a different model/dimension, this throws — switching the
    /// embedding would make existing vectors meaningless. The metadata is persisted
    /// so the guard survives restarts.
    /// </summary>
    Task InitializeAsync(string modelId, int dimension, CancellationToken ct = default);

    Task<DomainInfo> CreateDomainAsync(string name, string description = "", CancellationToken ct = default);
    Task<LabelInfo> CreateLabelAsync(string name, string description = "", CancellationToken ct = default);
    Task<IReadOnlyList<DomainInfo>> ListDomainsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<LabelInfo>> ListLabelsAsync(CancellationToken ct = default);

    // --- profiles & guardrails (persisted config; replace-the-whole-set semantics) ---
    // CRUD lives in RagClient (an in-memory cache); the store just persists/loads the
    // full list, so a backend only needs to serialize two small JSON blobs.

    /// <summary>List the persisted profiles. Default: none (a store that doesn't persist config).</summary>
    Task<IReadOnlyList<ProfileInfo>> ListProfilesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ProfileInfo>>(Array.Empty<ProfileInfo>());

    /// <summary>Persist the full set of profiles (replaces the stored set). Default: no-op (not persisted).</summary>
    Task SaveProfilesAsync(IReadOnlyList<ProfileInfo> profiles, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>List the persisted guardrail rules. Default: none.</summary>
    Task<IReadOnlyList<GuardrailRule>> ListGuardrailsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<GuardrailRule>>(Array.Empty<GuardrailRule>());

    /// <summary>Persist the full set of guardrail rules (replaces the stored set). Default: no-op.</summary>
    Task SaveGuardrailsAsync(IReadOnlyList<GuardrailRule> guardrails, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Index one chunk (its vector + provenance and when it was ingested).</summary>
    Task AddChunkAsync(string source, string text, string? domain, IReadOnlyList<string> labels, float[] vector,
        DateTime ingestedAtUtc = default, CancellationToken ct = default);

    /// <summary>
    /// Index many chunks at once. The default loops over <see cref="AddChunkAsync"/>;
    /// backends override it to write the whole batch in a single round-trip.
    /// </summary>
    async Task AddChunksAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken ct = default)
    {
        foreach (var c in chunks)
            await AddChunkAsync(c.Source, c.Text, c.Domain, c.Labels, c.Vector, c.IngestedAtUtc, ct).ConfigureAwait(false);
    }

    /// <summary>Top-k by cosine similarity, optionally scoped to a domain and required labels.</summary>
    Task<IReadOnlyList<StoredHit>> SearchAsync(float[] query, int k, string? domain = null, IReadOnlyList<string>? labels = null, CancellationToken ct = default);

    /// <summary>Total chunks indexed.</summary>
    Task<int> CountAsync(CancellationToken ct = default);

    /// <summary>All stored chunks (without vectors) — used to (re)build the in-memory
    /// lexical index for hybrid search at startup.</summary>
    Task<IReadOnlyList<StoredChunk>> EnumerateAsync(CancellationToken ct = default);

    /// <summary>
    /// Delete every chunk indexed under <paramref name="source"/> (optionally scoped
    /// to a <paramref name="domain"/> — omit to delete across all domains). Returns
    /// how many chunks were removed.
    /// </summary>
    Task<int> DeleteBySourceAsync(string source, string? domain = null, CancellationToken ct = default);

    /// <summary>
    /// Aggregate stored chunks into documents (one entry per distinct <c>Source</c>,
    /// optionally scoped to a <paramref name="domain"/>). The default implementation
    /// groups the result of <see cref="EnumerateAsync"/> in-process; backends may
    /// override it to aggregate server-side instead.
    /// </summary>
    async Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(string? domain = null, CancellationToken ct = default)
    {
        var chunks = await EnumerateAsync(ct).ConfigureAwait(false);
        return chunks
            .Where(c => domain is null || string.Equals(c.Domain, domain, StringComparison.OrdinalIgnoreCase))
            .GroupBy(c => (c.Source, c.Domain))
            .Select(g => new DocumentInfo(g.Key.Source, g.Key.Domain, g.Count(), g.Max(c => c.IngestedAtUtc)))
            .ToList();
    }

    // --- generic catalog (optional key/value storage for consumer-owned metadata) ---
    // Lets an application persist its own small JSON blobs (e.g. an ingest manifest,
    // see IngestIfChangedAsync) alongside the vectors instead of a separate database,
    // without RagKit needing to know what "kind"/"key" mean. Default: not supported —
    // a store that doesn't override these simply can't back consumer-owned catalog data.

    /// <summary>Read a catalog entry, or null if absent. Default: unsupported (returns null).</summary>
    Task<string?> GetCatalogEntryAsync(string kind, string key, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    /// <summary>Write (create or replace) a catalog entry. Default: no-op (not persisted).</summary>
    Task SaveCatalogEntryAsync(string kind, string key, string json, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>Delete a catalog entry, if present. Default: no-op.</summary>
    Task DeleteCatalogEntryAsync(string kind, string key, CancellationToken ct = default)
        => Task.CompletedTask;
}

/// <summary>Thrown when a store is reopened with an embedding that doesn't match what created it.</summary>
public sealed class EmbeddingMismatchException : Exception
{
    public EmbeddingMismatchException(string message) : base(message) { }
}
