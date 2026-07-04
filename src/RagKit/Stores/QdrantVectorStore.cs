using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RagKit.Internal;

namespace RagKit;

/// <summary>
/// Qdrant-backed store over its REST API (no SDK dependency, only HttpClient).
/// Chunks live in one collection; a sibling "catalog" collection holds the
/// embedding guard (model/dimension) plus the domain and label definitions, so
/// all state — including the guard — lives in Qdrant itself.
/// </summary>
public sealed class QdrantVectorStore : IVectorStore
{
    private readonly HttpClient _http;
    private readonly string _chunks;
    private readonly string _catalog;
    private static readonly string MetaId = Uuid("__meta__");

    public QdrantVectorStore(string url = "http://127.0.0.1:6333", string collection = "ragkit", string? apiKey = null, HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.BaseAddress = new Uri(url.TrimEnd('/') + "/");
        if (!string.IsNullOrEmpty(apiKey)) _http.DefaultRequestHeaders.Add("api-key", apiKey);
        _chunks = collection;
        _catalog = collection + "_catalog";
    }

    public async Task InitializeAsync(string modelId, int dimension, CancellationToken ct = default)
    {
        await EnsureCollectionAsync(_catalog, 1, ct).ConfigureAwait(false);

        // Guard: compare against the persisted meta point.
        var meta = await GetPointPayloadAsync(_catalog, MetaId, ct).ConfigureAwait(false);
        if (meta is not null &&
            meta.Value.TryGetProperty("modelId", out var m) &&
            meta.Value.TryGetProperty("dimension", out var d))
        {
            var prevModel = m.GetString();
            var prevDim = d.GetInt32();
            if (prevModel is not null && (prevModel != modelId || prevDim != dimension))
                throw new EmbeddingMismatchException(
                    $"La colección Qdrant '{_chunks}' se creó con '{prevModel}' (dim {prevDim}) " +
                    $"y se intenta abrir con '{modelId}' (dim {dimension}). Usa el mismo embedding o una colección nueva.");
        }

        var existingDim = await EnsureCollectionAsync(_chunks, dimension, ct).ConfigureAwait(false);
        if (existingDim is int ed && ed != dimension)
            throw new EmbeddingMismatchException(
                $"La colección Qdrant '{_chunks}' tiene dimensión {ed}, pero el embedding usa {dimension}.");

        await UpsertAsync(_catalog, MetaId, new float[] { 0f },
            new { kind = "meta", modelId, dimension }, ct).ConfigureAwait(false);
    }

    public async Task<DomainInfo> CreateDomainAsync(string name, string description = "", CancellationToken ct = default)
    {
        await UpsertAsync(_catalog, Uuid("domain:" + name), new float[] { 0f },
            new { kind = "domain", name, description }, ct).ConfigureAwait(false);
        return new DomainInfo(name, description);
    }

    public async Task<LabelInfo> CreateLabelAsync(string name, string description = "", CancellationToken ct = default)
    {
        await UpsertAsync(_catalog, Uuid("label:" + name), new float[] { 0f },
            new { kind = "label", name, description }, ct).ConfigureAwait(false);
        return new LabelInfo(name, description);
    }

    public async Task<IReadOnlyList<DomainInfo>> ListDomainsAsync(CancellationToken ct = default)
    {
        var pts = await ScrollByKindAsync(_catalog, "domain", ct).ConfigureAwait(false);
        return pts.Select(p => new DomainInfo(Str(p, "name"), Str(p, "description"))).ToList();
    }

    public async Task<IReadOnlyList<LabelInfo>> ListLabelsAsync(CancellationToken ct = default)
    {
        var pts = await ScrollByKindAsync(_catalog, "label", ct).ConfigureAwait(false);
        return pts.Select(p => new LabelInfo(Str(p, "name"), Str(p, "description"))).ToList();
    }

    public async Task<IReadOnlyList<ProfileInfo>> ListProfilesAsync(CancellationToken ct = default)
        => Deserialize<ProfileInfo>(await GetSettingAsync("profiles", ct).ConfigureAwait(false));

    public Task SaveProfilesAsync(IReadOnlyList<ProfileInfo> profiles, CancellationToken ct = default)
        => SaveSettingAsync("profiles", JsonSerializer.Serialize(profiles), ct);

    public async Task<IReadOnlyList<GuardrailRule>> ListGuardrailsAsync(CancellationToken ct = default)
        => Deserialize<GuardrailRule>(await GetSettingAsync("guardrails", ct).ConfigureAwait(false));

    public Task SaveGuardrailsAsync(IReadOnlyList<GuardrailRule> guardrails, CancellationToken ct = default)
        => SaveSettingAsync("guardrails", JsonSerializer.Serialize(guardrails), ct);

    private Task SaveSettingAsync(string name, string json, CancellationToken ct)
        => UpsertAsync(_catalog, Uuid("settings:" + name), new float[] { 0f },
            new { kind = "settings", name, json }, ct);

    private async Task<string?> GetSettingAsync(string name, CancellationToken ct)
    {
        var pay = await GetPointPayloadAsync(_catalog, Uuid("settings:" + name), ct).ConfigureAwait(false);
        return pay is null ? null : Str(pay.Value, "json");
    }

    private static IReadOnlyList<T> Deserialize<T>(string? json)
        => string.IsNullOrWhiteSpace(json) ? Array.Empty<T>() : JsonSerializer.Deserialize<List<T>>(json) ?? new List<T>();

    public Task AddChunkAsync(string source, string text, string? domain, IReadOnlyList<string> labels, float[] vector,
        DateTime ingestedAtUtc = default, CancellationToken ct = default)
        => UpsertAsync(_chunks, Guid.NewGuid().ToString(), vector,
            new { source, text, domain, labels = labels.ToArray(), ingestedAtUtc = ingestedAtUtc.ToString("o") }, ct);

    public async Task<IReadOnlyList<StoredHit>> SearchAsync(float[] query, int k, string? domain = null, IReadOnlyList<string>? labels = null, CancellationToken ct = default)
    {
        var must = new List<object>();
        if (domain != null) must.Add(new { key = "domain", match = new { value = domain } });
        if (labels is { Count: > 0 })
            foreach (var l in labels) must.Add(new { key = "labels", match = new { value = l } });

        object body = must.Count > 0
            ? new { vector = query, limit = k, with_payload = true, filter = new { must } }
            : new { vector = query, limit = k, with_payload = true };

        using var resp = await _http.PostAsJsonAsync($"collections/{_chunks}/points/search", body, ct).ConfigureAwait(false);
        var json = await ReadAsync(resp, ct).ConfigureAwait(false);
        var hits = new List<StoredHit>();
        foreach (var p in json.RootElement.GetProperty("result").EnumerateArray())
        {
            var pay = p.GetProperty("payload");
            var labelList = pay.TryGetProperty("labels", out var la) && la.ValueKind == JsonValueKind.Array
                ? la.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                : new List<string>();
            hits.Add(new StoredHit(
                Str(pay, "source"), Str(pay, "text"),
                pay.TryGetProperty("domain", out var dm) && dm.ValueKind == JsonValueKind.String ? dm.GetString() : null,
                labelList, p.GetProperty("score").GetDouble(), IdOf(p)));
        }
        return hits;
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync($"collections/{_chunks}/points/count", new { exact = true }, ct).ConfigureAwait(false);
        var json = await ReadAsync(resp, ct).ConfigureAwait(false);
        return json.RootElement.GetProperty("result").GetProperty("count").GetInt32();
    }

    public async Task<IReadOnlyList<StoredChunk>> EnumerateAsync(CancellationToken ct = default)
    {
        // Single large scroll (enough for typical corpora; paginate for very large ones).
        using var resp = await _http.PostAsJsonAsync($"collections/{_chunks}/points/scroll",
            new { limit = 10000, with_payload = true }, ct).ConfigureAwait(false);
        var json = await ReadAsync(resp, ct).ConfigureAwait(false);
        var outList = new List<StoredChunk>();
        foreach (var p in json.RootElement.GetProperty("result").GetProperty("points").EnumerateArray())
        {
            var pay = p.GetProperty("payload");
            var labels = pay.TryGetProperty("labels", out var la) && la.ValueKind == JsonValueKind.Array
                ? la.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                : new List<string>();
            outList.Add(new StoredChunk(Str(pay, "source"), Str(pay, "text"),
                pay.TryGetProperty("domain", out var dm) && dm.ValueKind == JsonValueKind.String ? dm.GetString() : null,
                labels, ParseIngestedAt(pay), IdOf(p)));
        }
        return outList;
    }

    private static DateTime ParseIngestedAt(JsonElement pay)
        => pay.TryGetProperty("ingestedAtUtc", out var t) && t.ValueKind == JsonValueKind.String
            && DateTime.TryParse(t.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt : default;

    /// <summary>A point's own id, as Qdrant echoes it back — string (UUID) or number, both
    /// stringified so callers get a uniform opaque identifier.</summary>
    private static string IdOf(JsonElement point)
    {
        var id = point.GetProperty("id");
        return id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : id.GetRawText();
    }

    // Generic consumer catalog: distinct "catalog:" id prefix so a caller-chosen
    // kind/key (e.g. kind="domain") can never collide with the internal domain/label/
    // settings points, which are namespaced under "domain:"/"label:"/"settings:".
    public async Task<string?> GetCatalogEntryAsync(string kind, string key, CancellationToken ct = default)
    {
        var pay = await GetPointPayloadAsync(_catalog, Uuid($"catalog:{kind}:{key}"), ct).ConfigureAwait(false);
        return pay is null ? null : Str(pay.Value, "json");
    }

    public Task SaveCatalogEntryAsync(string kind, string key, string json, CancellationToken ct = default)
        => UpsertAsync(_catalog, Uuid($"catalog:{kind}:{key}"), new float[] { 0f },
            new { kind = "catalog", entryKind = kind, key, json }, ct);

    public async Task DeleteCatalogEntryAsync(string kind, string key, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync($"collections/{_catalog}/points/delete?wait=true",
            new { points = new[] { Uuid($"catalog:{kind}:{key}") } }, ct).ConfigureAwait(false);
        await ReadAsync(resp, ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteBySourceAsync(string source, string? domain = null, CancellationToken ct = default)
    {
        var must = new List<object> { new { key = "source", match = new { value = source } } };
        if (domain != null) must.Add(new { key = "domain", match = new { value = domain } });
        var filter = new { must };

        int count;
        using (var countResp = await _http.PostAsJsonAsync($"collections/{_chunks}/points/count",
            new { filter, exact = true }, ct).ConfigureAwait(false))
        {
            var countJson = await ReadAsync(countResp, ct).ConfigureAwait(false);
            count = countJson.RootElement.GetProperty("result").GetProperty("count").GetInt32();
            if (count == 0) return 0;
        }

        using var delResp = await _http.PostAsJsonAsync($"collections/{_chunks}/points/delete?wait=true",
            new { filter }, ct).ConfigureAwait(false);
        await ReadAsync(delResp, ct).ConfigureAwait(false);
        return count;
    }

    public async Task<int> DeleteByDomainAsync(string domain, CancellationToken ct = default)
    {
        var filter = new { must = new[] { new { key = "domain", match = new { value = domain } } } };

        int count;
        using (var countResp = await _http.PostAsJsonAsync($"collections/{_chunks}/points/count",
            new { filter, exact = true }, ct).ConfigureAwait(false))
        {
            var countJson = await ReadAsync(countResp, ct).ConfigureAwait(false);
            count = countJson.RootElement.GetProperty("result").GetProperty("count").GetInt32();
            if (count == 0) return 0;
        }

        using var delResp = await _http.PostAsJsonAsync($"collections/{_chunks}/points/delete?wait=true",
            new { filter }, ct).ConfigureAwait(false);
        await ReadAsync(delResp, ct).ConfigureAwait(false);
        return count;
    }

    public async Task<bool> DeleteDomainAsync(string name, CancellationToken ct = default)
    {
        var id = Uuid("domain:" + name);
        var existing = await GetPointPayloadAsync(_catalog, id, ct).ConfigureAwait(false);
        if (existing is null) return false;

        using var resp = await _http.PostAsJsonAsync($"collections/{_catalog}/points/delete?wait=true",
            new { points = new[] { id } }, ct).ConfigureAwait(false);
        await ReadAsync(resp, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<ChunkPage> ListChunksAsync(string source, string? domain = null, int take = 100, string? cursor = null, CancellationToken ct = default)
    {
        var must = new List<object> { new { key = "source", match = new { value = source } } };
        if (domain != null) must.Add(new { key = "domain", match = new { value = domain } });

        object body = cursor is null
            ? new { filter = new { must }, limit = take, with_payload = true }
            : new { filter = new { must }, limit = take, with_payload = true, offset = cursor };

        using var resp = await _http.PostAsJsonAsync($"collections/{_chunks}/points/scroll", body, ct).ConfigureAwait(false);
        var json = await ReadAsync(resp, ct).ConfigureAwait(false);
        var result = json.RootElement.GetProperty("result");

        var items = new List<StoredChunk>();
        foreach (var p in result.GetProperty("points").EnumerateArray())
        {
            var pay = p.GetProperty("payload");
            var labels = pay.TryGetProperty("labels", out var la) && la.ValueKind == JsonValueKind.Array
                ? la.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                : new List<string>();
            items.Add(new StoredChunk(Str(pay, "source"), Str(pay, "text"),
                pay.TryGetProperty("domain", out var dm) && dm.ValueKind == JsonValueKind.String ? dm.GetString() : null,
                labels, ParseIngestedAt(pay), IdOf(p)));
        }

        string? next = result.TryGetProperty("next_page_offset", out var npo) && npo.ValueKind != JsonValueKind.Null
            ? (npo.ValueKind == JsonValueKind.String ? npo.GetString() : npo.GetRawText())
            : null;
        return new ChunkPage(items, next);
    }

    // --- REST helpers --------------------------------------------------------

    /// <summary>Create the collection if missing; return its current vector size (or null if just created).</summary>
    private async Task<int?> EnsureCollectionAsync(string name, int size, CancellationToken ct)
    {
        using (var get = await _http.GetAsync($"collections/{name}", ct).ConfigureAwait(false))
        {
            if (get.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                return doc.RootElement.GetProperty("result").GetProperty("config")
                    .GetProperty("params").GetProperty("vectors").GetProperty("size").GetInt32();
            }
        }
        using var put = await _http.PutAsJsonAsync($"collections/{name}",
            new { vectors = new { size, distance = "Cosine" } }, ct).ConfigureAwait(false);
        await ReadAsync(put, ct).ConfigureAwait(false);
        return null;
    }

    private async Task UpsertAsync(string collection, string id, float[] vector, object payload, CancellationToken ct)
    {
        var body = new { points = new[] { new { id, vector, payload } } };
        using var resp = await _http.PutAsJsonAsync($"collections/{collection}/points?wait=true", body, ct).ConfigureAwait(false);
        await ReadAsync(resp, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement?> GetPointPayloadAsync(string collection, string id, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"collections/{collection}/points/{id}", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var result = doc.RootElement.GetProperty("result");
        return result.TryGetProperty("payload", out var pay) ? pay.Clone() : null;
    }

    private async Task<List<JsonElement>> ScrollByKindAsync(string collection, string kind, CancellationToken ct)
    {
        var body = new { filter = new { must = new[] { new { key = "kind", match = new { value = kind } } } }, limit = 1000, with_payload = true };
        using var resp = await _http.PostAsJsonAsync($"collections/{collection}/points/scroll", body, ct).ConfigureAwait(false);
        var json = await ReadAsync(resp, ct).ConfigureAwait(false);
        var list = new List<JsonElement>();
        foreach (var p in json.RootElement.GetProperty("result").GetProperty("points").EnumerateArray())
            list.Add(p.GetProperty("payload").Clone());
        return list;
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new RagKitException($"Qdrant respondió {(int)resp.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    private static string Str(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Deterministic UUID from a string (so catalog ids are stable).</summary>
    private static string Uuid(string s)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(s));
        return new Guid(hash).ToString();
    }
}
