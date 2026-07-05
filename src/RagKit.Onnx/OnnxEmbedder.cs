using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using RagKit;

namespace RagKit.Onnx;

/// <summary>Enables the ONNX embedder so <c>EmbedderKind.Onnx</c> works via the factory.</summary>
public static class OnnxEmbedding
{
    /// <summary>Register the ONNX embedder. <c>EmbedderConfig.Model</c> must point to a folder
    /// (or model.onnx path) containing <c>model.onnx</c> and <c>vocab.txt</c>.</summary>
    public static void Enable() =>
        EmbedderFactory.Register(EmbedderKind.Onnx,
            cfg => new OnnxEmbedder(cfg.Model ?? throw new InvalidOperationException(
                "EmbedderConfig.Model debe ser la carpeta del modelo ONNX (con model.onnx y vocab.txt).")));

    // A small, well-known sentence-transformers ONNX export with a WordPiece
    // vocab.txt (compatible with our BERT tokenizer): all-MiniLM-L6-v2, 384-dim.
    private const string DefaultModelName = "all-MiniLM-L6-v2";
    private static readonly (string Url, string File)[] DefaultModelFiles =
    {
        ("https://huggingface.co/Xenova/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx", "model.onnx"),
        ("https://huggingface.co/Xenova/all-MiniLM-L6-v2/resolve/main/vocab.txt", "vocab.txt"),
    };

    // ~100-language sentence-transformer (SentencePiece) fine-tuned for retrieval;
    // meaningfully better than the English-only default above on non-English corpora
    // (e.g. Spanish) — the quantized export keeps the download small. 384-dim.
    private const string MultilingualModelName = "multilingual-e5-small";
    private static readonly (string Url, string File)[] MultilingualModelFiles =
    {
        ("https://huggingface.co/Xenova/multilingual-e5-small/resolve/main/onnx/model_quantized.onnx", "model.onnx"),
        ("https://huggingface.co/Xenova/multilingual-e5-small/resolve/main/sentencepiece.bpe.model", "sentencepiece.bpe.model"),
    };

    // Larger multilingual model (XLM-RoBERTa-large backbone, 1024-dim) — meaningfully
    // stronger retrieval than multilingual-e5-small, at the cost of a bigger/slower
    // model. This specific export (int8-quantized) bakes CLS-pooling + normalization
    // into a second "logits" output alongside the raw "last_hidden_state" (verified via
    // InferenceSession.OutputMetadata) — OnnxEmbedder detects and prefers it automatically,
    // so PoolingStrategy.Cls below is really just documentation/a safety net for a future
    // BGE-M3 export that only exposes raw hidden states. No query:/passage: prefix needed
    // (unlike e5) — BGE-M3 doesn't require one for retrieval.
    private const string BgeM3ModelName = "bge-m3";
    private static readonly (string Url, string File)[] BgeM3ModelFiles =
    {
        ("https://huggingface.co/hotchpotch/vespa-onnx-BAAI-bge-m3-only-dense/resolve/main/BAAI-bge-m3_quantized.onnx", "model.onnx"),
        ("https://huggingface.co/hotchpotch/vespa-onnx-BAAI-bge-m3-only-dense/resolve/main/sentencepiece.bpe.model", "sentencepiece.bpe.model"),
    };

    /// <summary>
    /// The zero-config "real" embedder: downloads a small sentence-transformers ONNX
    /// model into a local cache the first time (then reused offline) and returns an
    /// <see cref="EmbedderConfig"/> ready to assign to <c>RagOptions.Embedder</c>.
    /// Semantic and private (runs in-process), unlike the non-semantic core fallback.
    /// English-oriented — for other languages use <see cref="UseMultilingualDefaultModelAsync"/>
    /// or point <c>EmbedderConfig.Model</c> at a model of your own choice.
    /// </summary>
    /// <param name="cacheDir">Where to cache the model. Default: per-user local app data.</param>
    public static Task<EmbedderConfig> UseDefaultModelAsync(string? cacheDir = null, CancellationToken ct = default)
        => DownloadModelAsync(DefaultModelName, DefaultModelFiles, useQueryPassagePrefix: false, PoolingStrategy.Mean, cacheDir, ct);

    /// <summary>
    /// Same zero-config idea as <see cref="UseDefaultModelAsync"/>, but downloads
    /// <c>multilingual-e5-small</c> (SentencePiece, ~100 languages incl. Spanish)
    /// instead — meaningfully better retrieval on non-English corpora than the
    /// English-oriented default. Configures the mandatory <c>query:</c>/<c>passage:</c>
    /// prefix this model family requires (see <see cref="OnnxEmbedder(string,bool)"/>)
    /// automatically — callers don't need to know about it.
    /// </summary>
    /// <param name="cacheDir">Where to cache the model. Default: per-user local app data.</param>
    public static Task<EmbedderConfig> UseMultilingualDefaultModelAsync(string? cacheDir = null, CancellationToken ct = default)
        => DownloadModelAsync(MultilingualModelName, MultilingualModelFiles, useQueryPassagePrefix: true, PoolingStrategy.Mean, cacheDir, ct);

    /// <summary>
    /// Same zero-config idea as <see cref="UseDefaultModelAsync"/>, but downloads
    /// <b>BGE-M3</b> (int8-quantized, dense-only export) instead — a bigger, more
    /// capable multilingual model than <c>multilingual-e5-small</c> (1024-dim vs
    /// 384-dim), at the cost of a larger download and slower CPU inference. No
    /// query:/passage: prefix needed (unlike e5). This only uses BGE-M3's dense
    /// embedding — its sparse/ColBERT outputs aren't exposed through this preset.
    /// </summary>
    /// <param name="cacheDir">Where to cache the model. Default: per-user local app data.</param>
    public static Task<EmbedderConfig> UseBgeM3DefaultModelAsync(string? cacheDir = null, CancellationToken ct = default)
        => DownloadModelAsync(BgeM3ModelName, BgeM3ModelFiles, useQueryPassagePrefix: false, PoolingStrategy.Cls, cacheDir, ct);

    private static async Task<EmbedderConfig> DownloadModelAsync(
        string modelName, (string Url, string File)[] files, bool useQueryPassagePrefix, PoolingStrategy pooling,
        string? cacheDir, CancellationToken ct)
    {
        Enable();
        cacheDir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RagKit", "models", modelName);
        Directory.CreateDirectory(cacheDir);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        foreach (var (url, file) in files)
        {
            var dest = Path.Combine(cacheDir, file);
            if (File.Exists(dest) && new FileInfo(dest).Length > 0) continue;
            await DownloadAsync(http, url, dest, ct).ConfigureAwait(false);
        }
        // Instance (not Kind/Model) so the prefix/pooling settings travel with the
        // embedder without needing new EmbedderConfig fields for them.
        return new EmbedderConfig { Instance = new OnnxEmbedder(cacheDir, useQueryPassagePrefix, pooling) };
    }

    private static async Task DownloadAsync(HttpClient http, string url, string dest, CancellationToken ct)
    {
        var tmp = dest + ".part";
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"No se pudo descargar el modelo ONNX desde {url} (HTTP {(int)resp.StatusCode}).");
            await using (var fs = File.Create(tmp))
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            File.Move(tmp, dest, overwrite: true);
        }
        catch
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            throw;
        }
    }
}

/// <summary>Which token(s) of the raw per-token output represent the sentence, when the
/// ONNX graph doesn't already bake pooling into a dedicated output (see <see
/// cref="OnnxEmbedder"/>'s output-resolution logic). <see cref="Mean"/> fits
/// sentence-transformers-style models (MiniLM, e5); <see cref="Cls"/> fits BGE-style
/// models, which use the first token's hidden state as the sentence representation.</summary>
public enum PoolingStrategy
{
    /// <summary>Average the hidden state over every non-padding token. Default —
    /// correct for all-MiniLM-L6-v2 and the e5 family.</summary>
    Mean,
    /// <summary>Take the hidden state of the first token ([CLS]/&lt;s&gt;) as-is.
    /// Correct for BGE-family models (e.g. BGE-M3).</summary>
    Cls,
}

/// <summary>
/// Local, in-process embedder running a sentence-transformers ONNX model with mean
/// pooling over the token embeddings and L2 normalization. The tokenizer is picked
/// from the model folder: <b>WordPiece</b> (<c>vocab.txt</c>, e.g. all-MiniLM-L6-v2)
/// or <b>SentencePiece</b> (<c>sentencepiece.bpe.model</c>/<c>spiece.model</c>/
/// <c>tokenizer.model</c>, e.g. multilingual-e5). No external service; privacy stays on-prem.
/// </summary>
public sealed class OnnxEmbedder : IEmbedder, IDisposable
{
    private static readonly string[] SentencePieceFiles = { "sentencepiece.bpe.model", "spiece.model", "tokenizer.model" };

    private readonly string _modelPath;
    private readonly string _tokenizerPath;
    private readonly bool _isSentencePiece;
    private readonly bool _useQueryPassagePrefix;
    private readonly PoolingStrategy _pooling;
    private InferenceSession? _session;
    private Tokenizer? _tokenizer;
    private bool _needsTokenTypes;
    private string? _pooledOutputName;    // set if the graph already exposes a 2-D (batch, hidden) output
    private string? _hiddenStateOutputName; // the raw 3-D (batch, seq, hidden) output, pooled here if no pooled one exists

    /// <param name="modelDirOrPath">Model folder (or direct path to <c>model.onnx</c>).</param>
    /// <param name="useQueryPassagePrefix">
    /// The e5 family (e.g. multilingual-e5-small) is an asymmetric bi-encoder: it was
    /// contrastively trained expecting every embedded text to be prefixed with
    /// <c>"query: "</c> (a question) or <c>"passage: "</c> (indexed content) — without it,
    /// query and passage vectors don't align the way the model learned. Set this to
    /// <c>true</c> for e5-family models (done automatically by <see
    /// cref="OnnxEmbedding.UseMultilingualDefaultModelAsync"/>); leave <c>false</c> (default)
    /// for models with no such convention, e.g. all-MiniLM-L6-v2.
    /// </param>
    /// <param name="pooling">
    /// How to collapse the raw per-token output into one sentence vector, for graphs
    /// that don't already expose a pooled output themselves (see <see
    /// cref="ResolveOutputNames"/>). Ignored when the graph does its own pooling —
    /// e.g. the BGE-M3 export <see cref="OnnxEmbedding.UseBgeM3DefaultModelAsync"/>
    /// downloads exposes a ready-pooled <c>logits</c> output alongside the raw
    /// <c>last_hidden_state</c>, so this parameter never comes into play for it.
    /// </param>
    public OnnxEmbedder(string modelDirOrPath, bool useQueryPassagePrefix = false, PoolingStrategy pooling = PoolingStrategy.Mean)
    {
        _useQueryPassagePrefix = useQueryPassagePrefix;
        _pooling = pooling;
        _modelPath = File.Exists(modelDirOrPath) ? modelDirOrPath : Path.Combine(modelDirOrPath, "model.onnx");
        if (!File.Exists(_modelPath))
            throw new FileNotFoundException($"No se encontró el modelo ONNX en '{_modelPath}'.");
        var dir = Path.GetDirectoryName(Path.GetFullPath(_modelPath))!;

        var vocab = Path.Combine(dir, "vocab.txt");
        var sp = SentencePieceFiles.Select(f => Path.Combine(dir, f)).FirstOrDefault(File.Exists);
        if (File.Exists(vocab)) { _tokenizerPath = vocab; _isSentencePiece = false; }
        else if (sp is not null) { _tokenizerPath = sp; _isSentencePiece = true; }
        else throw new FileNotFoundException(
            $"No se encontró tokenizer (vocab.txt o sentencepiece.*/spiece.model/tokenizer.model) junto al modelo en '{dir}'.");

        ModelId = "onnx:" + new DirectoryInfo(dir).Name;
    }

    public string ModelId { get; }
    public int Dimension { get; private set; }

    /// <summary>Load the model + tokenizer off the calling thread and probe the dimension.</summary>
    public Task InitializeAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        if (_session is not null) return;
        _tokenizer = _isSentencePiece ? CreateSentencePiece(_tokenizerPath) : BertTokenizer.Create(_tokenizerPath);
        _session = new InferenceSession(_modelPath);
        _needsTokenTypes = _session.InputMetadata.ContainsKey("token_type_ids");
        (_pooledOutputName, _hiddenStateOutputName) = ResolveOutputNames(_session.OutputMetadata);
        Dimension = EmbedCore("warmup").Length;
    }, ct);

    /// <summary>
    /// Some ONNX exports (e.g. the Vespa-oriented BGE-M3 build <see
    /// cref="OnnxEmbedding.UseBgeM3DefaultModelAsync"/> downloads) bake pooling into
    /// the graph and expose an already-pooled 2-D <c>(batch, hidden)</c> output — often
    /// called <c>logits</c> — alongside the raw per-token 3-D <c>(batch, seq, hidden)</c>
    /// one (typically <c>last_hidden_state</c>). Picking the 2-D one when present uses
    /// whatever pooling/projection the model's own author intended, instead of guessing;
    /// the 3-D one is kept as the fallback for graphs (MiniLM, e5) that only expose raw
    /// hidden states and rely on us to pool (see <see cref="PoolingStrategy"/>).
    /// </summary>
    private static (string? Pooled, string? HiddenState) ResolveOutputNames(IReadOnlyDictionary<string, NodeMetadata> outputs)
    {
        string? pooled = null, hidden = null;
        foreach (var (name, meta) in outputs)
        {
            switch (meta.Dimensions.Length)
            {
                case 2 when pooled is null: pooled = name; break;
                case 3 when hidden is null: hidden = name; break;
            }
        }
        if (pooled is null && hidden is null)
            throw new NotSupportedException(
                "El modelo ONNX no expone ni una salida 2-D (ya pooleada) ni una 3-D (per-token) reconocible.");
        return (pooled, hidden);
    }

    private static Tokenizer CreateSentencePiece(string path)
    {
        using var fs = File.OpenRead(path);
        return SentencePieceTokenizer.Create(fs, addBeginningOfSentence: true, addEndOfSentence: true, specialTokens: null);
    }

    /// <summary>Embeds a query (a question). RagClient only ever calls this single-item
    /// overload for the user's question — see <see cref="EmbedBatchAsync"/> for passages.</summary>
    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) =>
        Task.Run(() => EmbedCore(_useQueryPassagePrefix ? "query: " + text : text), ct);

    /// <summary>Batch: inference is thread-safe, so run chunks across cores. RagClient only
    /// ever calls this for chunks being indexed (passages) — see <see cref="EmbedAsync"/> for queries.</summary>
    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var result = new float[texts.Count][];
        await Parallel.ForAsync(0, texts.Count,
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
            (i, _) =>
            {
                result[i] = EmbedCore(_useQueryPassagePrefix ? "passage: " + texts[i] : texts[i]);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
        return result;
    }

    private float[] EmbedCore(string text)
    {
        if (_session is null || _tokenizer is null)
            throw new InvalidOperationException("Llama a InitializeAsync antes de usar el embedder ONNX.");
        var ids = _tokenizer.EncodeToIds(text);
        int n = Math.Max(1, ids.Count);
        var idArr = new long[n];
        var mask = new long[n];
        var types = new long[n];
        for (int i = 0; i < ids.Count; i++) { idArr[i] = ids[i]; mask[i] = 1; }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(idArr, new[] { 1, n })),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, new[] { 1, n })),
        };
        if (_needsTokenTypes)
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(types, new[] { 1, n })));

        // InferenceSession.Run is thread-safe, so no lock: bulk ingest runs concurrently.
        using var results = _session.Run(inputs);
        var vec = _pooledOutputName is not null
            ? ReadPooled(results, _pooledOutputName)
            : Pool(results, _hiddenStateOutputName!, mask);

        double norm = 0;
        foreach (var x in vec) norm += (double)x * x;
        norm = Math.Sqrt(norm);
        if (norm > 0) for (int d = 0; d < vec.Length; d++) vec[d] = (float)(vec[d] / norm);
        return vec;
    }

    /// <summary>The graph already pooled — copy its (batch, hidden) output as-is.</summary>
    private static float[] ReadPooled(IReadOnlyCollection<DisposableNamedOnnxValue> results, string name)
    {
        var pooled = results.First(r => r.Name == name).AsTensor<float>(); // [1, hidden]
        var vec = new float[pooled.Dimensions[1]];
        for (int d = 0; d < vec.Length; d++) vec[d] = pooled[0, d];
        return vec;
    }

    /// <summary>No pooled output available — collapse the raw per-token (batch, seq, hidden)
    /// output ourselves, per <see cref="_pooling"/>.</summary>
    private float[] Pool(IReadOnlyCollection<DisposableNamedOnnxValue> results, string name, long[] mask)
    {
        var output = results.First(r => r.Name == name).AsTensor<float>(); // [1, seq, hidden]
        int seq = output.Dimensions[1];
        int hidden = output.Dimensions[2];
        var vec = new float[hidden];

        if (_pooling == PoolingStrategy.Cls)
        {
            for (int d = 0; d < hidden; d++) vec[d] = output[0, 0, d]; // position 0 = [CLS]/<s>, always valid
            return vec;
        }

        int valid = 0;
        for (int i = 0; i < seq; i++)
        {
            if (mask[i] == 0) continue;
            valid++;
            for (int d = 0; d < hidden; d++) vec[d] += output[0, i, d];
        }
        if (valid > 0) for (int d = 0; d < hidden; d++) vec[d] /= valid;
        return vec;
    }

    public void Dispose() => _session?.Dispose();
}
