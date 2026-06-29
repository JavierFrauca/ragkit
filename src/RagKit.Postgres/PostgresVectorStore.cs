using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using RagKit;

namespace RagKit.Postgres;

/// <summary>Enables the Postgres backend so <c>VectorStoreKind.Postgres</c> works via the factory.</summary>
public static class PostgresStore
{
    /// <summary>Register the Postgres connector. Call once at startup.</summary>
    public static void Enable() =>
        VectorStoreFactory.Register(VectorStoreKind.Postgres,
            cfg => new PostgresVectorStore(
                cfg.ConnectionString ?? throw new InvalidOperationException("StoreConfig.ConnectionString es obligatorio para Postgres."),
                cfg.Collection));
}

/// <summary>
/// PostgreSQL + pgvector store. Chunks live in <c>{collection}_chunks</c> with a
/// <c>vector(dim)</c> column; domains/labels and the embedding guard (model/dim)
/// live in sibling tables — all in the database, identical contract to the others.
/// </summary>
public sealed class PostgresVectorStore : IVectorStore
{
    private readonly NpgsqlDataSource _ds;
    private readonly string _c;

    public PostgresVectorStore(string connectionString, string collection = "ragkit")
    {
        _ds = NpgsqlDataSource.Create(connectionString);
        _c = Sanitize(collection);
    }

    private string Chunks => $"{_c}_chunks";
    private string Meta => $"{_c}_meta";
    private string Domains => $"{_c}_domains";
    private string Labels => $"{_c}_labels";
    private string Settings => $"{_c}_settings";

    public async Task InitializeAsync(string modelId, int dimension, CancellationToken ct = default)
    {
        await Exec($"""
            CREATE EXTENSION IF NOT EXISTS vector;
            CREATE TABLE IF NOT EXISTS {Meta} (id int PRIMARY KEY DEFAULT 1, model_id text, dimension int);
            CREATE TABLE IF NOT EXISTS {Domains} (name text PRIMARY KEY, description text);
            CREATE TABLE IF NOT EXISTS {Labels} (name text PRIMARY KEY, description text);
            CREATE TABLE IF NOT EXISTS {Settings} (name text PRIMARY KEY, value text);
            CREATE TABLE IF NOT EXISTS {Chunks} (
                id uuid PRIMARY KEY, source text, body text, domain text, labels text[], embedding vector({dimension}));
            """, ct).ConfigureAwait(false);

        await using (var read = _ds.CreateCommand($"SELECT model_id, dimension FROM {Meta} WHERE id = 1"))
        await using (var r = await read.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await r.ReadAsync(ct).ConfigureAwait(false) && !r.IsDBNull(0))
            {
                var prevModel = r.GetString(0);
                var prevDim = r.GetInt32(1);
                if (prevModel != modelId || prevDim != dimension)
                    throw new EmbeddingMismatchException(
                        $"La base Postgres '{_c}' se creó con '{prevModel}' (dim {prevDim}) y se intenta abrir con '{modelId}' (dim {dimension}).");
            }
        }

        await Exec($"""
            INSERT INTO {Meta} (id, model_id, dimension) VALUES (1, @m, @d)
            ON CONFLICT (id) DO UPDATE SET model_id = EXCLUDED.model_id, dimension = EXCLUDED.dimension;
            """, ct, ("m", modelId), ("d", dimension)).ConfigureAwait(false);
    }

    public async Task<DomainInfo> CreateDomainAsync(string name, string description = "", CancellationToken ct = default)
    {
        await Exec($"INSERT INTO {Domains}(name,description) VALUES(@n,@d) ON CONFLICT (name) DO UPDATE SET description=EXCLUDED.description",
            ct, ("n", name), ("d", description)).ConfigureAwait(false);
        return new DomainInfo(name, description);
    }

    public async Task<LabelInfo> CreateLabelAsync(string name, string description = "", CancellationToken ct = default)
    {
        await Exec($"INSERT INTO {Labels}(name,description) VALUES(@n,@d) ON CONFLICT (name) DO NOTHING",
            ct, ("n", name), ("d", description)).ConfigureAwait(false);
        return new LabelInfo(name, description);
    }

    public async Task<IReadOnlyList<DomainInfo>> ListDomainsAsync(CancellationToken ct = default)
    {
        var list = new List<DomainInfo>();
        await using var cmd = _ds.CreateCommand($"SELECT name, COALESCE(description,'') FROM {Domains} ORDER BY name");
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(new DomainInfo(r.GetString(0), r.GetString(1)));
        return list;
    }

    public async Task<IReadOnlyList<LabelInfo>> ListLabelsAsync(CancellationToken ct = default)
    {
        var list = new List<LabelInfo>();
        await using var cmd = _ds.CreateCommand($"SELECT name, COALESCE(description,'') FROM {Labels} ORDER BY name");
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

    private Task SaveSettingAsync(string name, string value, CancellationToken ct)
        => Exec($"INSERT INTO {Settings}(name,value) VALUES(@n,@v) ON CONFLICT (name) DO UPDATE SET value=EXCLUDED.value",
            ct, ("n", name), ("v", value));

    private async Task<string?> GetSettingAsync(string name, CancellationToken ct)
    {
        await using var cmd = _ds.CreateCommand($"SELECT value FROM {Settings} WHERE name=@n");
        cmd.Parameters.AddWithValue("n", name);
        var r = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return r as string;
    }

    private static IReadOnlyList<T> Deserialize<T>(string? json)
        => string.IsNullOrWhiteSpace(json) ? Array.Empty<T>() : JsonSerializer.Deserialize<List<T>>(json) ?? new List<T>();

    public async Task AddChunkAsync(string source, string text, string? domain, IReadOnlyList<string> labels, float[] vector, CancellationToken ct = default)
    {
        await using var cmd = _ds.CreateCommand(
            $"INSERT INTO {Chunks}(id,source,body,domain,labels,embedding) VALUES(@id,@s,@b,@dom,@lbl,@emb::vector)");
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("s", source);
        cmd.Parameters.AddWithValue("b", text);
        cmd.Parameters.AddWithValue("dom", (object?)domain ?? DBNull.Value);
        cmd.Parameters.AddWithValue("lbl", labels.ToArray());
        cmd.Parameters.AddWithValue("emb", Literal(vector));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredHit>> SearchAsync(float[] query, int k, string? domain = null, IReadOnlyList<string>? labels = null, CancellationToken ct = default)
    {
        var req = labels?.ToArray() ?? Array.Empty<string>();
        await using var cmd = _ds.CreateCommand($"""
            SELECT source, body, domain, labels, 1 - (embedding <=> @q::vector) AS score
            FROM {Chunks}
            WHERE (@dom IS NULL OR domain = @dom)
              AND (cardinality(@req) = 0 OR labels @> @req)
            ORDER BY embedding <=> @q::vector
            LIMIT @k
            """);
        cmd.Parameters.AddWithValue("q", Literal(query));
        cmd.Parameters.AddWithValue("dom", (object?)domain ?? DBNull.Value);
        cmd.Parameters.AddWithValue("req", req);
        cmd.Parameters.AddWithValue("k", k);
        var hits = new List<StoredHit>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var labelsOut = r.IsDBNull(3) ? Array.Empty<string>() : (string[])r.GetValue(3);
            hits.Add(new StoredHit(r.GetString(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2), labelsOut, r.GetDouble(4)));
        }
        return hits;
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var cmd = _ds.CreateCommand($"SELECT count(*) FROM {Chunks}");
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<StoredChunk>> EnumerateAsync(CancellationToken ct = default)
    {
        var list = new List<StoredChunk>();
        await using var cmd = _ds.CreateCommand($"SELECT source, body, domain, labels FROM {Chunks}");
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var labels = r.IsDBNull(3) ? Array.Empty<string>() : (string[])r.GetValue(3);
            list.Add(new StoredChunk(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), labels));
        }
        return list;
    }

    // --- helpers -------------------------------------------------------------

    private async Task Exec(string sql, CancellationToken ct, params (string, object)[] ps)
    {
        await using var cmd = _ds.CreateCommand(sql);
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
        return string.IsNullOrEmpty(clean) ? "ragkit" : clean.ToLowerInvariant();
    }
}
