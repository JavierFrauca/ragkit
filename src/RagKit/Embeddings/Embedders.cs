using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RagKit.Internal;

namespace RagKit;

/// <summary>Which embedding backend to use.</summary>
public enum EmbedderKind
{
    /// <summary>Built-in local embedder. The zero-config default.</summary>
    Local,
    /// <summary>Local ONNX model (connector — see factory).</summary>
    Onnx,
    /// <summary>Hosted embeddings via an OpenAI-compatible API (connector — see factory).</summary>
    OpenAi,
}

/// <summary>Embedder configuration (used by the factory).</summary>
public sealed class EmbedderConfig
{
    /// <summary>
    /// A ready embedder instance to use directly. When set, it wins over
    /// <see cref="Kind"/> and the factory — no enum, no global <c>Enable()</c>,
    /// just compile-time-checked injection (e.g. your own <see cref="IEmbedder"/>,
    /// or the one returned by a connector). This is the no-magic advanced path.
    /// </summary>
    public IEmbedder? Instance { get; set; }

    public EmbedderKind Kind { get; set; } = EmbedderKind.Local;
    /// <summary>For Onnx: model folder/path. For OpenAi: model name (e.g. "nomic-embed-text").</summary>
    public string? Model { get; set; }
    /// <summary>For OpenAi: embeddings endpoint base URL (e.g. Ollama "http://localhost:11434/v1").</summary>
    public string? Url { get; set; }
    public string? ApiKey { get; set; }
    /// <summary>For OpenAi: the vector dimension. If 0, it is probed once at startup.</summary>
    public int Dimension { get; set; }
    /// <summary>For OpenAi: override for <see cref="IEmbedder.MaxChunkChars"/> — not
    /// auto-detectable for a generic hosted endpoint. Null keeps the 1000 default.</summary>
    public int? MaxChunkChars { get; set; }
}

/// <summary>
/// Builds the configured <see cref="IEmbedder"/>. Backends register a builder for
/// their <see cref="EmbedderKind"/>; Local is built in and connector packages
/// (e.g. RagKit.Onnx) register themselves via <see cref="Register"/>, keeping the
/// core free of heavy inference dependencies.
/// </summary>
public static class EmbedderFactory
{
    private static readonly Dictionary<EmbedderKind, Func<EmbedderConfig, IEmbedder>> Registry = new()
    {
        [EmbedderKind.Local] = _ => new LocalEmbedder(),
        [EmbedderKind.OpenAi] = cfg => new ApiEmbedder(cfg),
    };

    /// <summary>Register (or replace) the builder for an embedder kind. Called by connector packages.</summary>
    public static void Register(EmbedderKind kind, Func<EmbedderConfig, IEmbedder> builder) => Registry[kind] = builder;

    public static IEmbedder Create(EmbedderConfig? config)
    {
        config ??= new EmbedderConfig();
        if (config.Instance is not null) return config.Instance;
        if (Registry.TryGetValue(config.Kind, out var builder)) return builder(config);
        throw new NotSupportedException(
            $"El embedder '{config.Kind}' no está registrado. Referencia su paquete (p. ej. RagKit.Onnx) " +
            "y llama a su método Enable(), o regístralo con EmbedderFactory.Register(...).");
    }
}

/// <summary>
/// Built-in local embedder: a deterministic bag-of-words hash vector. No model
/// download, works offline — so the whole product builds and runs with zero
/// setup. It is NOT semantically strong; the real default (a small multilingual
/// ONNX model) plugs in behind <see cref="IEmbedder"/> via <see cref="EmbedderKind.Onnx"/>.
/// </summary>
public sealed class LocalEmbedder : IEmbedder
{
    private readonly int _dim;

    public LocalEmbedder(int dim = 256) => _dim = Math.Max(1, dim);

    public string ModelId => $"local-hash-v1-{_dim}";
    public int Dimension => _dim;

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var v = new float[_dim];
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            uint h = 2166136261u; // FNV-1a
            foreach (var b in System.Text.Encoding.UTF8.GetBytes(token.ToLowerInvariant()))
            {
                h ^= b;
                h *= 16777619u;
            }
            v[h % (uint)_dim] += 1f;
        }
        Vec.Normalize(v);
        return Task.FromResult(v);
    }
}

/// <summary>
/// Embedder over an OpenAI-compatible <c>/embeddings</c> endpoint — works with
/// OpenAI, Cohere/Voyage-compatible gateways and **local servers like Ollama**
/// (e.g. <c>nomic-embed-text</c> at <c>http://localhost:11434/v1</c>). Only
/// HttpClient; no SDK. Set <c>Dimension</c> to skip the one-time startup probe.
/// </summary>
public sealed class ApiEmbedder : IEmbedder
{
    private readonly HttpClient _http;
    private readonly string _model;

    public ApiEmbedder(EmbedderConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.Url) || string.IsNullOrWhiteSpace(cfg.Model))
            throw new InvalidOperationException("El embedder por API necesita Url y Model (p. ej. Ollama + nomic-embed-text).");
        _http = new HttpClient { BaseAddress = new Uri(cfg.Url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(60) };
        if (!string.IsNullOrEmpty(cfg.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
        _model = cfg.Model;
        ModelId = "api:" + cfg.Model;
        Dimension = cfg.Dimension; // known up front, or probed in InitializeAsync
        MaxChunkChars = cfg.MaxChunkChars ?? 1000;
    }

    public string ModelId { get; }
    public int Dimension { get; private set; }
    public int MaxChunkChars { get; }

    /// <summary>Probe the vector dimension once (async, no constructor network call).</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (Dimension > 0) return;
        Dimension = (await EmbedAsync("warmup", ct).ConfigureAwait(false)).Length;
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => (await EmbedManyAsync(new[] { text }, ct).ConfigureAwait(false))[0];

    /// <summary>Real batch: a single request with an array input (one round-trip).</summary>
    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        => texts.Count == 0 ? Task.FromResult<IReadOnlyList<float[]>>(Array.Empty<float[]>()) : EmbedManyAsync(texts, ct);

    private async Task<IReadOnlyList<float[]>> EmbedManyAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { model = _model, input = texts });
        using var resp = await HttpRetry.PostAsync(_http, "embeddings",
            () => new StringContent(payload, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new RagKitException($"El endpoint de embeddings respondió {(int)resp.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data");
        var result = new float[texts.Count][];
        foreach (var item in data.EnumerateArray())
        {
            int idx = item.TryGetProperty("index", out var ix) ? ix.GetInt32() : Array.IndexOf(result, null);
            var arr = item.GetProperty("embedding");
            var v = new float[arr.GetArrayLength()];
            int i = 0;
            foreach (var x in arr.EnumerateArray()) v[i++] = x.GetSingle();
            result[idx] = v;
        }
        return result;
    }
}
