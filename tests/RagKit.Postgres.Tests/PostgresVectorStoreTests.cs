using RagKit.Postgres;

namespace RagKit.Postgres.Tests;

/// <summary>
/// Integration tests against a real Postgres+pgvector instance.
/// Self-skip when RAGKIT_POSTGRES_CONNECTION is not set.
///
/// To run locally:
///   docker compose up -d postgres --wait
///   export RAGKIT_POSTGRES_CONNECTION="Host=127.0.0.1;Port=5432;Database=ragkit;Username=ragkit;Password=ragkit"
///   dotnet test tests/RagKit.Postgres.Tests
/// </summary>
public class PostgresVectorStoreTests : IAsyncLifetime
{
    private readonly string _connectionString;
    private PostgresVectorStore? _store;
    private readonly string _collection = "ragkit_test_" + Guid.NewGuid().ToString("N")[..8];

    public PostgresVectorStoreTests()
    {
        _connectionString = Environment.GetEnvironmentVariable("RAGKIT_POSTGRES_CONNECTION") ?? "";
    }

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return;
        _store = new PostgresVectorStore(_connectionString, _collection);
        await _store.InitializeAsync("test-model", 16);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task InitializeAsync_creates_tables()
    {
        if (_store is null) return; // skip

        // Second InitializeAsync should not throw (idempotent)
        await _store.InitializeAsync("test-model", 16);
    }

    [Fact]
    public async Task InitializeAsync_rejects_different_model()
    {
        if (_store is null) return;
        await Assert.ThrowsAsync<EmbeddingMismatchException>(
            () => _store.InitializeAsync("other-model", 16));
    }

    [Fact]
    public async Task Add_and_search_chunk()
    {
        if (_store is null) return;

        var vector = Enumerable.Range(0, 16).Select(i => (float)i / 16).ToArray();
        await _store.AddChunkAsync("doc.txt", "El IVA es del 21 por ciento.", "fiscal",
            new[] { "iva" }, vector);

        var hits = await _store.SearchAsync(vector, k: 5, domain: "fiscal");
        Assert.Single(hits);
        Assert.Equal("doc.txt", hits[0].Source);
        Assert.Contains("iva", hits[0].Labels);
    }

    [Fact]
    public async Task Search_filters_by_labels()
    {
        if (_store is null) return;

        var v1 = Enumerable.Range(0, 16).Select(i => (float)i / 16).ToArray();
        await _store.AddChunkAsync("a.txt", "IVA general 21%", "fiscal", new[] { "iva" }, v1);
        await _store.AddChunkAsync("b.txt", "Contrato indefinido", "rrhh", new[] { "contrato" }, v1);

        var hits = await _store.SearchAsync(v1, k: 5, labels: new[] { "iva" });
        Assert.Single(hits);
        Assert.Contains("iva", hits[0].Labels);
    }

    [Fact]
    public async Task CountAsync_returns_chunk_count()
    {
        if (_store is null) return;

        var v = Enumerable.Range(0, 16).Select(i => (float)i).ToArray();
        await _store.AddChunkAsync("a.txt", "texto", "d", Array.Empty<string>(), v);
        await _store.AddChunkAsync("b.txt", "texto", "d", Array.Empty<string>(), v);

        Assert.Equal(2, await _store.CountAsync());
    }

    [Fact]
    public async Task ListChunksAsync_with_cursor_pagination()
    {
        if (_store is null) return;

        var v = Enumerable.Range(0, 16).Select(i => (float)i).ToArray();
        for (int i = 0; i < 10; i++)
            await _store.AddChunkAsync($"doc{i}.txt", $"chunk {i}", "d", Array.Empty<string>(), v,
                chunkIndex: i);

        var page1 = await _store.ListChunksAsync("doc", take: 3);
        Assert.Equal(3, page1.Items.Count);
        Assert.NotNull(page1.NextCursor);

        var page2 = await _store.ListChunksAsync("doc", take: 3, cursor: page1.NextCursor);
        Assert.True(page2.Items.Count > 0);
    }

    [Fact]
    public async Task Catalog_save_and_get()
    {
        if (_store is null) return;

        await _store.SaveCatalogEntryAsync("manifests", "doc1", "{\"hash\":\"abc\"}");
        var entry = await _store.GetCatalogEntryAsync("manifests", "doc1");

        Assert.NotNull(entry);
        Assert.Contains("abc", entry);
    }
}
