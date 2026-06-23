using System.Text.Json;
using RagKit.Internal;

namespace RagKit;

/// <summary>
/// Zero-setup default store: vectors in memory, catalog (embedding guard +
/// domains + labels) persisted to a JSON file under the data path so the
/// model/dimension guard survives restarts. (Vectors themselves are ephemeral;
/// for durable vectors use a real backend like Qdrant.)
/// </summary>
public sealed class InMemoryVectorStore : IVectorStore
{
    private sealed record Item(string Source, string Text, string? Domain, string[] Labels, float[] Vec);

    private sealed class Meta
    {
        public string? ModelId { get; set; }
        public int Dimension { get; set; }
        public List<DomainInfo> Domains { get; set; } = new();
        public List<LabelInfo> Labels { get; set; } = new();
    }

    private readonly string _metaPath;
    private readonly object _lock = new();
    private readonly List<Item> _items = new();
    private Meta _meta = new();
    private bool _initialized;

    public InMemoryVectorStore(string dataPath = "./ragkit-data")
    {
        Directory.CreateDirectory(dataPath);
        _metaPath = Path.Combine(dataPath, "ragkit.meta.json");
    }

    public Task InitializeAsync(string modelId, int dimension, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (File.Exists(_metaPath))
            {
                var existing = JsonSerializer.Deserialize<Meta>(File.ReadAllText(_metaPath)) ?? new Meta();
                if (existing.ModelId is not null &&
                    (existing.ModelId != modelId || existing.Dimension != dimension))
                {
                    throw new EmbeddingMismatchException(
                        $"La base se creó con el embedding '{existing.ModelId}' (dim {existing.Dimension}) " +
                        $"y ahora se intenta abrir con '{modelId}' (dim {dimension}). " +
                        "Cambiar el embedding invalidaría los vectores existentes: usa el mismo o crea una base nueva.");
                }
                _meta = existing;
            }
            _meta.ModelId = modelId;
            _meta.Dimension = dimension;
            _initialized = true;
            Save();
        }
        return Task.CompletedTask;
    }

    public Task<DomainInfo> CreateDomainAsync(string name, string description = "", CancellationToken ct = default)
    {
        var info = new DomainInfo(name, description);
        lock (_lock)
        {
            EnsureInit();
            _meta.Domains.RemoveAll(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
            _meta.Domains.Add(info);
            Save();
        }
        return Task.FromResult(info);
    }

    public Task<LabelInfo> CreateLabelAsync(string name, string description = "", CancellationToken ct = default)
    {
        var info = new LabelInfo(name, description);
        lock (_lock)
        {
            EnsureInit();
            if (!_meta.Labels.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)))
                _meta.Labels.Add(info);
            Save();
        }
        return Task.FromResult(info);
    }

    public Task<IReadOnlyList<DomainInfo>> ListDomainsAsync(CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult<IReadOnlyList<DomainInfo>>(_meta.Domains.ToList());
    }

    public Task<IReadOnlyList<LabelInfo>> ListLabelsAsync(CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult<IReadOnlyList<LabelInfo>>(_meta.Labels.ToList());
    }

    public Task AddChunkAsync(string source, string text, string? domain, IReadOnlyList<string> labels, float[] vector, CancellationToken ct = default)
    {
        lock (_lock)
        {
            EnsureInit();
            _items.Add(new Item(source, text, domain, labels.ToArray(), vector));
        }
        return Task.CompletedTask;
    }

    public Task AddChunksAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken ct = default)
    {
        lock (_lock)
        {
            EnsureInit();
            foreach (var c in chunks)
                _items.Add(new Item(c.Source, c.Text, c.Domain, c.Labels.ToArray(), c.Vector));
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoredHit>> SearchAsync(float[] query, int k, string? domain = null, IReadOnlyList<string>? labels = null, CancellationToken ct = default)
    {
        List<StoredHit> scored;
        lock (_lock)
        {
            scored = new List<StoredHit>();
            foreach (var it in _items)
            {
                if (domain != null && !string.Equals(it.Domain, domain, StringComparison.OrdinalIgnoreCase)) continue;
                if (labels is { Count: > 0 } && !labels.All(l => it.Labels.Contains(l, StringComparer.OrdinalIgnoreCase))) continue;
                scored.Add(new StoredHit(it.Source, it.Text, it.Domain, it.Labels, Vec.Dot(query, it.Vec)));
            }
        }
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (scored.Count > k) scored.RemoveRange(k, scored.Count - k);
        return Task.FromResult<IReadOnlyList<StoredHit>>(scored);
    }

    public Task<int> CountAsync(CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_items.Count);
    }

    public Task<IReadOnlyList<StoredChunk>> EnumerateAsync(CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<StoredChunk>>(
                _items.Select(i => new StoredChunk(i.Source, i.Text, i.Domain, i.Labels)).ToList());
    }

    private void EnsureInit()
    {
        if (!_initialized) throw new InvalidOperationException("Llama a InitializeAsync antes de usar la base.");
    }

    private void Save() =>
        File.WriteAllText(_metaPath, JsonSerializer.Serialize(_meta, new JsonSerializerOptions { WriteIndented = true }));
}
