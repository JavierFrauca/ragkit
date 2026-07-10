using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using RagKit;

namespace RagKit.SqlServer;

/// <summary>Enables the SQL Server backend so <c>VectorStoreKind.SqlServer</c> works via the factory.</summary>
public static class SqlServerStore
{
    /// <summary>Register the SQL Server connector. Call once at startup.</summary>
    public static void Enable() =>
        VectorStoreFactory.Register(VectorStoreKind.SqlServer,
            cfg => new SqlServerVectorStore(
                cfg.ConnectionString ?? throw new InvalidOperationException("StoreConfig.ConnectionString es obligatorio para SQL Server."),
                cfg.Collection));
}

/// <summary>
/// SQL Server 2025 store using the native <c>VECTOR</c> type and
/// <c>VECTOR_DISTANCE('cosine', …)</c>. Chunks in <c>{collection}_chunks</c>;
/// domains/labels and the embedding guard in sibling tables. Labels are stored as
/// JSON and the "must contain all" filter is applied exactly in SQL via
/// <c>OPENJSON</c> (see <see cref="SearchAsync"/>), not over-fetched and
/// filtered in-process.
/// </summary>
public sealed class SqlServerVectorStore : IVectorStore
{
    private readonly string _cs;
    private readonly string _c;
    private int _dim;

    public SqlServerVectorStore(string connectionString, string collection = "ragkit")
    {
        _cs = connectionString;
        _c = Sanitize(collection);
    }

    private string Chunks => $"{_c}_chunks";
    private string Meta => $"{_c}_meta";
    private string Domains => $"{_c}_domains";
    private string Labels => $"{_c}_labels";
    private string Settings => $"{_c}_settings";
    private string Catalog => $"{_c}_catalog";

    public async Task InitializeAsync(string modelId, int dimension, CancellationToken ct = default)
    {
        _dim = dimension;
        await using var con = await OpenAsync(ct).ConfigureAwait(false);

        await Exec(con, $"""
            IF OBJECT_ID('{Meta}') IS NULL CREATE TABLE {Meta}(id int PRIMARY KEY, model_id nvarchar(200), dimension int);
            IF OBJECT_ID('{Domains}') IS NULL CREATE TABLE {Domains}(name nvarchar(200) PRIMARY KEY, description nvarchar(max));
            IF OBJECT_ID('{Labels}') IS NULL CREATE TABLE {Labels}(name nvarchar(200) PRIMARY KEY, description nvarchar(max));
            IF OBJECT_ID('{Settings}') IS NULL CREATE TABLE {Settings}(name nvarchar(100) PRIMARY KEY, value nvarchar(max));
            IF OBJECT_ID('{Catalog}') IS NULL CREATE TABLE {Catalog}(kind nvarchar(100), key_name nvarchar(400), value nvarchar(max), PRIMARY KEY(kind, key_name));
            IF OBJECT_ID('{Chunks}') IS NULL CREATE TABLE {Chunks}(
                id uniqueidentifier PRIMARY KEY, source nvarchar(400), body nvarchar(max),
                domain nvarchar(200) NULL, labels nvarchar(max) NULL, embedding vector({dimension}),
                ingested_at_utc datetime2 NULL);
            IF COL_LENGTH('{Chunks}', 'chunk_index') IS NULL ALTER TABLE {Chunks} ADD chunk_index int NULL;
            """, ct).ConfigureAwait(false);

        await using (var read = new SqlCommand($"SELECT model_id, dimension FROM {Meta} WHERE id=1", con))
        await using (var r = await read.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await r.ReadAsync(ct).ConfigureAwait(false) && !await r.IsDBNullAsync(0, ct).ConfigureAwait(false))
            {
                var prevModel = r.GetString(0);
                var prevDim = r.GetInt32(1);
                if (prevModel != modelId || prevDim != dimension)
                    throw new EmbeddingMismatchException(
                        $"La base SQL Server '{_c}' se creó con '{prevModel}' (dim {prevDim}) y se intenta abrir con '{modelId}' (dim {dimension}).");
            }
        }

        await Exec(con, $"""
            MERGE {Meta} AS t USING (SELECT 1 AS id) AS s ON t.id=s.id
            WHEN MATCHED THEN UPDATE SET model_id=@m, dimension=@d
            WHEN NOT MATCHED THEN INSERT(id,model_id,dimension) VALUES(1,@m,@d);
            """, ct, ("@m", modelId), ("@d", dimension)).ConfigureAwait(false);
    }

    public async Task<DomainInfo> CreateDomainAsync(string name, string description = "", CancellationToken ct = default)
    {
        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        await Exec(con, $"""
            MERGE {Domains} AS t USING (SELECT @n AS name) AS s ON t.name=s.name
            WHEN MATCHED THEN UPDATE SET description=@d
            WHEN NOT MATCHED THEN INSERT(name,description) VALUES(@n,@d);
            """, ct, ("@n", name), ("@d", description)).ConfigureAwait(false);
        return new DomainInfo(name, description);
    }

    public async Task<LabelInfo> CreateLabelAsync(string name, string description = "", CancellationToken ct = default)
    {
        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        await Exec(con, $"IF NOT EXISTS(SELECT 1 FROM {Labels} WHERE name=@n) INSERT INTO {Labels}(name,description) VALUES(@n,@d)",
            ct, ("@n", name), ("@d", description)).ConfigureAwait(false);
        return new LabelInfo(name, description);
    }

    public async Task<IReadOnlyList<DomainInfo>> ListDomainsAsync(CancellationToken ct = default)
    {
        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        var list = new List<DomainInfo>();
        await using var cmd = new SqlCommand($"SELECT name, ISNULL(description,'') FROM {Domains} ORDER BY name", con);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(new DomainInfo(r.GetString(0), r.GetString(1)));
        return list;
    }

    public async Task<IReadOnlyList<LabelInfo>> ListLabelsAsync(CancellationToken ct = default)
    {
        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        var list = new List<LabelInfo>();
        await using var cmd = new SqlCommand($"SELECT name, ISNULL(description,'') FROM {Labels} ORDER BY name", con);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(new LabelInfo(r.GetString(0), r.GetString(1)));
        return list;
    }

    public async Task<IReadOnlyList<ProfileInfo>> ListProfilesAsync(CancellationToken ct = default)
        => Deserialize<ProfileInfo>(await GetSettingAsync("profiles", ct).ConfigureAwait(false));

    public Task SaveProfilesAsync(IReadOnlyList<ProfileInfo> profiles, CancellationToken ct = default)
        => SaveSettingAsync("profiles", JsonSerializer.Serialize(profiles), ct);

    public async Task<IReadOnlyList<GuardrailRule>> ListGuardrailsAsync(CancellationToken ct = default)
        => Deserialize<GuardrailRule>(await GetSettingAsync("guardrails", ct).ConfigureAwait(false));

    public Task SaveGuardrailsAsync(IReadOnlyList<GuardrailRule> guardrails, CancellationToken ct = default)
        => SaveSettingAsync("guardrails", JsonSerializer.Serialize(guardrails), ct);

    private async Task SaveSettingAsync(string name, string value, CancellationToken ct)
    {
        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        await Exec(con, $"""
            MERGE {Settings} AS t USING (SELECT @n AS name) AS s ON t.name=s.name
            WHEN MATCHED THEN UPDATE SET value=@v
            WHEN NOT MATCHED THEN INSERT(name,value) VALUES(@n,@v);
            """, ct, ("@n", name), ("@v", value)).ConfigureAwait(false);
    }

    private async Task<string?> GetSettingAsync(string name, CancellationToken ct)
    {
        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand($"SELECT value FROM {Settings} WHERE name=@n", con);
        cmd.Parameters.AddWithValue("@n", name);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    private static IReadOnlyList<T> Deserialize<T>(string? json)
        => string.IsNullOrWhiteSpace(json) ? Array.Empty<T>() : JsonSerializer.Deserialize<List<T>>(json) ?? new List<T>();

    public async Task AddChunkAsync(string source, string text, string? domain, IReadOnlyList<string> labels, float[] vector,
        DateTime ingestedAtUtc = default, int chunkIndex = -1, CancellationToken ct = default)
    {
        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand(
            $"INSERT INTO {Chunks}(id,source,body,domain,labels,embedding,ingested_at_utc,chunk_index) VALUES(@id,@s,@b,@dom,@lbl,CAST(@emb AS vector({_dim})),@ing,@ci)", con);
        cmd.Parameters.AddWithValue("@id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("@s", source);
        cmd.Parameters.AddWithValue("@b", text);
        cmd.Parameters.AddWithValue("@dom", (object?)domain ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lbl", JsonSerializer.Serialize(labels));
        cmd.Parameters.AddWithValue("@emb", Literal(vector));
        cmd.Parameters.AddWithValue("@ing", ingestedAtUtc == default ? (object)DBNull.Value : ingestedAtUtc);
        cmd.Parameters.AddWithValue("@ci", chunkIndex);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // SQL Server caps parameters per statement at 2100; 7 params/row keeps well under that
    // even for a pathologically large single ingest, while still writing everything over
    // one connection instead of one connection per chunk.
    private const int BatchRows = 200;

    /// <summary>Write the whole batch as one or more multi-row <c>INSERT</c>s over a single
    /// connection (one round-trip per <see cref="BatchRows"/> chunks) instead of the default
    /// per-chunk loop, which opened a new connection for every single chunk.</summary>
    public async Task AddChunksAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken ct = default)
    {
        if (chunks.Count == 0) return;
        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        foreach (var batch in chunks.Chunk(BatchRows))
        {
            var values = new List<string>(batch.Length);
            await using var cmd = new SqlCommand { Connection = con };
            for (int i = 0; i < batch.Length; i++)
            {
                var c = batch[i];
                values.Add($"(@id{i},@s{i},@b{i},@dom{i},@lbl{i},CAST(@emb{i} AS vector({_dim})),@ing{i},@ci{i})");
                cmd.Parameters.AddWithValue($"@id{i}", Guid.NewGuid());
                cmd.Parameters.AddWithValue($"@s{i}", c.Source);
                cmd.Parameters.AddWithValue($"@b{i}", c.Text);
                cmd.Parameters.AddWithValue($"@dom{i}", (object?)c.Domain ?? DBNull.Value);
                cmd.Parameters.AddWithValue($"@lbl{i}", JsonSerializer.Serialize(c.Labels));
                cmd.Parameters.AddWithValue($"@emb{i}", Literal(c.Vector));
                cmd.Parameters.AddWithValue($"@ing{i}", c.IngestedAtUtc == default ? (object)DBNull.Value : c.IngestedAtUtc);
                cmd.Parameters.AddWithValue($"@ci{i}", c.ChunkIndex);
            }
            cmd.CommandText = $"INSERT INTO {Chunks}(id,source,body,domain,labels,embedding,ingested_at_utc,chunk_index) VALUES {string.Join(",", values)}";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<StoredHit>> SearchAsync(float[] query, int k, string? domain = null, IReadOnlyList<string>? labels = null, CancellationToken ct = default)
    {
        var req = labels ?? Array.Empty<string>();
        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        // "Contains all" is pushed into the query itself via a double-NOT-EXISTS (relational
        // division) over OPENJSON, instead of over-fetching and filtering in process: TOP(@k)
        // now sees only rows that already satisfy the label constraint, so it's an exact top-k
        // rather than a best-effort one bounded by how much we happened to over-fetch.
        await using var cmd = new SqlCommand($"""
            SELECT TOP(@k) source, body, domain, labels,
                   1 - VECTOR_DISTANCE('cosine', CAST(@q AS vector({_dim})), embedding) AS score, id
            FROM {Chunks}
            WHERE (@dom IS NULL OR domain=@dom)
              AND (@reqJson IS NULL OR NOT EXISTS (
                    SELECT 1 FROM OPENJSON(@reqJson) AS want
                    WHERE NOT EXISTS (
                        SELECT 1 FROM OPENJSON(ISNULL(labels, '[]')) AS have
                        WHERE have.value = want.value
                    )
                  ))
            ORDER BY VECTOR_DISTANCE('cosine', CAST(@q AS vector({_dim})), embedding)
            """, con);
        cmd.Parameters.AddWithValue("@k", k);
        cmd.Parameters.AddWithValue("@q", Literal(query));
        cmd.Parameters.AddWithValue("@dom", (object?)domain ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@reqJson", req.Count > 0 ? JsonSerializer.Serialize(req) : (object)DBNull.Value);

        var hits = new List<StoredHit>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var labelsOut = r.IsDBNull(3) ? Array.Empty<string>()
                : JsonSerializer.Deserialize<string[]>(r.GetString(3)) ?? Array.Empty<string>();
            hits.Add(new StoredHit(r.GetString(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2), labelsOut, r.GetDouble(4), r.GetGuid(5).ToString()));
        }
        return hits;
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand($"SELECT COUNT(*) FROM {Chunks}", con);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<StoredChunk>> EnumerateAsync(CancellationToken ct = default)
    {
        var list = new List<StoredChunk>();
        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand($"SELECT source, body, domain, labels, ingested_at_utc, id, chunk_index FROM {Chunks}", con);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var labels = r.IsDBNull(3) ? Array.Empty<string>()
                : JsonSerializer.Deserialize<string[]>(r.GetString(3)) ?? Array.Empty<string>();
            var ingestedAt = r.IsDBNull(4) ? default : r.GetDateTime(4);
            var chunkIndex = r.IsDBNull(6) ? -1 : r.GetInt32(6);
            list.Add(new StoredChunk(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), labels, ingestedAt, r.GetGuid(5).ToString(), chunkIndex));
        }
        return list;
    }

    public async Task<int> DeleteBySourceAsync(string source, string? domain = null, CancellationToken ct = default)
    {
        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand(
            $"DELETE FROM {Chunks} WHERE source=@s AND (@dom IS NULL OR domain=@dom)", con);
        cmd.Parameters.AddWithValue("@s", source);
        cmd.Parameters.AddWithValue("@dom", (object?)domain ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteByDomainAsync(string domain, CancellationToken ct = default)
    {
        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand($"DELETE FROM {Chunks} WHERE domain=@dom", con);
        cmd.Parameters.AddWithValue("@dom", domain);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteDomainAsync(string name, CancellationToken ct = default)
    {
        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand($"DELETE FROM {Domains} WHERE name=@n", con);
        cmd.Parameters.AddWithValue("@n", name);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public async Task<ChunkPage> ListChunksAsync(string source, string? domain = null, int take = 100, string? cursor = null, CancellationToken ct = default)
    {
        // Keyset pagination ordered by id: correct (no duplicates, no gaps) regardless
        // of GUID ordering semantics, as long as the order is stable across calls.
        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand($"""
            SELECT TOP(@take) source, body, domain, labels, ingested_at_utc, id, chunk_index
            FROM {Chunks}
            WHERE source=@s AND (@dom IS NULL OR domain=@dom) AND (@cur IS NULL OR id > @cur)
            ORDER BY id
            """, con);
        // Fetch one extra row over `take` so a page that happens to end exactly at the last
        // chunk isn't mistaken for "there might be more" — `items.Count == take` alone can't
        // tell a full-and-final page from a full-and-there's-more one.
        cmd.Parameters.AddWithValue("@take", take + 1);
        cmd.Parameters.AddWithValue("@s", source);
        cmd.Parameters.AddWithValue("@dom", (object?)domain ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cur", cursor is null ? DBNull.Value : Guid.Parse(cursor));

        var items = new List<StoredChunk>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var labels = r.IsDBNull(3) ? Array.Empty<string>()
                : JsonSerializer.Deserialize<string[]>(r.GetString(3)) ?? Array.Empty<string>();
            var ingestedAt = r.IsDBNull(4) ? default : r.GetDateTime(4);
            var id = r.GetGuid(5).ToString();
            var chunkIndex = r.IsDBNull(6) ? -1 : r.GetInt32(6);
            items.Add(new StoredChunk(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), labels, ingestedAt, id, chunkIndex));
        }
        string? next = items.Count > take ? items[take - 1].Id : null;
        if (items.Count > take) items.RemoveAt(items.Count - 1);
        return new ChunkPage(items, next);
    }

    public async Task<string?> GetCatalogEntryAsync(string kind, string key, CancellationToken ct = default)
    {
        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand($"SELECT value FROM {Catalog} WHERE kind=@k AND key_name=@n", con);
        cmd.Parameters.AddWithValue("@k", kind);
        cmd.Parameters.AddWithValue("@n", key);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    public async Task SaveCatalogEntryAsync(string kind, string key, string json, CancellationToken ct = default)
    {
        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        await Exec(con, $"""
            MERGE {Catalog} AS t USING (SELECT @k AS kind, @n AS key_name) AS s ON t.kind=s.kind AND t.key_name=s.key_name
            WHEN MATCHED THEN UPDATE SET value=@v
            WHEN NOT MATCHED THEN INSERT(kind,key_name,value) VALUES(@k,@n,@v);
            """, ct, ("@k", kind), ("@n", key), ("@v", json)).ConfigureAwait(false);
    }

    public async Task DeleteCatalogEntryAsync(string kind, string key, CancellationToken ct = default)
    {
        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        await Exec(con, $"DELETE FROM {Catalog} WHERE kind=@k AND key_name=@n", ct, ("@k", kind), ("@n", key)).ConfigureAwait(false);
    }

    // --- helpers -------------------------------------------------------------

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var con = new SqlConnection(_cs);
        await con.OpenAsync(ct).ConfigureAwait(false);
        return con;
    }

    private static async Task Exec(SqlConnection con, string sql, CancellationToken ct, params (string, object)[] ps)
    {
        await using var cmd = new SqlCommand(sql, con);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string Literal(float[] v)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < v.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(v[i].ToString(CultureInfo.InvariantCulture));
        }
        return sb.Append(']').ToString();
    }

    private static string Sanitize(string s)
    {
        var clean = new string(s.Where(ch => char.IsLetterOrDigit(ch) || ch == '_').ToArray());
        return string.IsNullOrEmpty(clean) ? "ragkit" : clean;
    }
}
