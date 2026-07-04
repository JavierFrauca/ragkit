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

/// <summary>A retrieved chunk with its score and provenance. <see cref="Id"/> is the
/// backend's own point/row id (empty when a backend predates it) — trailing default
/// so existing call sites keep compiling.</summary>
public sealed record StoredHit(string Source, string Text, string? Domain, IReadOnlyList<string> Labels, double Score, string Id = "");

/// <summary>A stored chunk (no score) — used to rebuild the lexical index for hybrid search,
/// to aggregate the document inventory (<see cref="IVectorStore.ListDocumentsAsync"/>), and
/// as the page item of <see cref="IVectorStore.ListChunksAsync"/> (where <see cref="Id"/> is
/// what a caller would use to reference this exact chunk later).</summary>
public sealed record StoredChunk(string Source, string Text, string? Domain, IReadOnlyList<string> Labels, DateTime IngestedAtUtc = default, string Id = "");

/// <summary>One page of <see cref="IVectorStore.ListChunksAsync"/>: the chunks plus an
/// opaque cursor to fetch the next page, or null when there isn't one. The cursor's
/// shape is backend-specific (an offset, a point id…) — callers must not parse it.</summary>
public sealed record ChunkPage(IReadOnlyList<StoredChunk> Items, string? NextCursor);

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
    /// <remarks>
    /// This is a correctness-preserving fallback, not a performance guarantee: a new
    /// <see cref="IVectorStore"/> implementation compiles fine without overriding it,
    /// but pays one round-trip per chunk. Override it for any backend expected to
    /// ingest at production volume.
    /// </remarks>
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
    /// Delete every chunk indexed under <paramref name="domain"/>, across every
    /// <c>source</c>. Returns how many chunks were removed. Does not remove the
    /// domain definition itself — see <see cref="DeleteDomainAsync"/>.
    /// </summary>
    Task<int> DeleteByDomainAsync(string domain, CancellationToken ct = default);

    /// <summary>
    /// Remove the domain definition itself (not its chunks — see <see
    /// cref="DeleteByDomainAsync"/>). Returns true if a domain with that name existed.
    /// </summary>
    Task<bool> DeleteDomainAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Aggregate stored chunks into documents (one entry per distinct <c>Source</c>,
    /// optionally scoped to a <paramref name="domain"/>). The default implementation
    /// groups the result of <see cref="EnumerateAsync"/> in-process; backends may
    /// override it to aggregate server-side instead.
    /// </summary>
    /// <remarks>
    /// This inherits whatever completeness and performance characteristics the
    /// backend's own <see cref="EnumerateAsync"/> has — if that implementation caps
    /// or paginates incompletely for very large collections, so does this. A new
    /// <see cref="IVectorStore"/> implementation compiles fine without overriding
    /// this method, so review it for backends expected to hold large collections.
    /// </remarks>
    async Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(string? domain = null, CancellationToken ct = default)
    {
        var chunks = await EnumerateAsync(ct).ConfigureAwait(false);
        return chunks
            .Where(c => domain is null || string.Equals(c.Domain, domain, StringComparison.OrdinalIgnoreCase))
            .GroupBy(c => (c.Source, c.Domain))
            .Select(g => new DocumentInfo(g.Key.Source, g.Key.Domain, g.Count(), g.Max(c => c.IngestedAtUtc)))
            .ToList();
    }

    /// <summary>
    /// One page of the chunks under <paramref name="source"/> (optionally scoped to a
    /// <paramref name="domain"/>), at most <paramref name="take"/> items. Pass the
    /// previous call's <see cref="ChunkPage.NextCursor"/> as <paramref name="cursor"/>
    /// to fetch the next page; start with <c>cursor: null</c>. The default
    /// implementation sorts <see cref="EnumerateAsync"/>'s result by <c>Id</c> and
    /// pages over it in-process (an integer offset as the cursor) — backends override
    /// it with native/keyset pagination instead of loading the whole collection.
    /// </summary>
    /// <remarks>
    /// A new <see cref="IVectorStore"/> implementation compiles fine without
    /// overriding this method, but every call loads the entire collection via
    /// <see cref="EnumerateAsync"/> to page over it in-process — override it with
    /// native pagination for any backend expected to hold a large collection.
    /// </remarks>
    async Task<ChunkPage> ListChunksAsync(string source, string? domain = null, int take = 100, string? cursor = null, CancellationToken ct = default)
    {
        var all = (await EnumerateAsync(ct).ConfigureAwait(false))
            .Where(c => string.Equals(c.Source, source, StringComparison.Ordinal)
                && (domain is null || string.Equals(c.Domain, domain, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .ToList();
        int offset = cursor is null ? 0 : int.Parse(cursor);
        var page = all.Skip(offset).Take(take).ToList();
        string? next = offset + page.Count < all.Count ? (offset + page.Count).ToString() : null;
        return new ChunkPage(page, next);
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
