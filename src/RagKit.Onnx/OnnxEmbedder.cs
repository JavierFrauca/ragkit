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
        => DownloadModelAsync(DefaultModelName, DefaultModelFiles, useQueryPassagePrefix: false, PoolingStrategy.Mean, maxChunkChars: 900, cacheDir, ct);

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
        => DownloadModelAsync(MultilingualModelName, MultilingualModelFiles, useQueryPassagePrefix: true, PoolingStrategy.Mean, maxChunkChars: 1800, cacheDir, ct);

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
        => DownloadModelAsync(BgeM3ModelName, BgeM3ModelFiles, useQueryPassagePrefix: false, PoolingStrategy.Cls, maxChunkChars: 24000, cacheDir, ct);

    private static async Task<EmbedderConfig> DownloadModelAsync(
        string modelName, (string Url, string File)[] files, bool useQueryPassagePrefix, PoolingStrategy pooling,
        int maxChunkChars, string? cacheDir, CancellationToken ct)
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
        return new EmbedderConfig { Instance = new OnnxEmbedder(cacheDir, useQueryPassagePrefix, pooling, maxChunkChars) };
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
    private bool _needsFairseqRemap; // see InitializeAsync — true for XLM-RoBERTa-family SentencePiece vocabs
    private readonly int? _maxBatchConcurrency; // ctor override; null = auto-heuristic, see InitializeAsync
    private int _batchConcurrency = 1; // resolved in InitializeAsync — see EmbedBatchAsync

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
    /// <param name="maxChunkChars">See <see cref="IEmbedder.MaxChunkChars"/> — the
    /// character budget for a chunk embedded with this model's token window. The
    /// three zero-config presets (<see cref="OnnxEmbedding"/>) pass in a value sized
    /// to their actual window; a manually constructed <see cref="OnnxEmbedder"/> for
    /// a different model should do the same.</param>
    /// <param name="maxBatchConcurrency">
    /// How many <see cref="EmbedBatchAsync"/> chunks run through <c>InferenceSession.Run</c>
    /// at once; each still gets its own internal thread budget (a fraction of <see
    /// cref="Environment.ProcessorCount"/>) so the two levels of parallelism don't multiply
    /// into oversubscription — see <see cref="InitializeAsync"/>. Default (<c>null</c>) is
    /// <c>1</c> (fully sequential, each call free to use every core): measured against a real
    /// 618K-char document on a 12-core machine, this was ~40% FASTER overall than splitting
    /// into several concurrent calls with fewer threads each — BGE-family models parallelize
    /// well within one call, so added batch-level concurrency mostly just adds cross-call
    /// cache/memory contention instead of real overlap. Raise this only if you've measured it
    /// helping for your specific model/hardware. On a container with a fractional/low CPU
    /// quota (e.g. Docker <c>cpus: 1.0</c>), <see cref="Environment.ProcessorCount"/> usually
    /// still reports the host's full core count, not the quota — any value above <c>1</c> risks
    /// cgroup-throttling instead of real parallelism, which reads as ingestion "stuck" with
    /// near-zero CPU%, not merely slow.
    /// </param>
    public OnnxEmbedder(string modelDirOrPath, bool useQueryPassagePrefix = false,
        PoolingStrategy pooling = PoolingStrategy.Mean, int maxChunkChars = 1000, int? maxBatchConcurrency = null)
    {
        _useQueryPassagePrefix = useQueryPassagePrefix;
        _pooling = pooling;
        MaxChunkChars = maxChunkChars;
        _maxBatchConcurrency = maxBatchConcurrency;
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

    public string ModelId { get; private set; }
    public int Dimension { get; private set; }
    public int MaxChunkChars { get; }

    /// <summary>Load the model + tokenizer off the calling thread and probe the dimension.</summary>
    public Task InitializeAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        if (_session is not null) return;
        _tokenizer = _isSentencePiece ? CreateSentencePiece(_tokenizerPath) : BertTokenizer.Create(_tokenizerPath);
        if (_isSentencePiece && _tokenizer is SentencePieceTokenizer spTok)
        {
            // Fairseq/XLM-RoBERTa fingerprint: the raw .model has unk=0, bos=1, eos=2 —
            // but the ONNX model's embedding table indexes <s>=0, <pad>=1, </s>=2, <unk>=3,
            // with every real token at raw_id+1 (the remap `transformers` applies when
            // training/exporting XLM-RoBERTa-family models — e.g. multilingual-e5-small,
            // BGE-M3 — never reflected in the raw SentencePiece vocab itself). Detected
            // here, not asked of the caller: this fixes every model with this fingerprint,
            // whichever preset or hand-picked path fed it into OnnxEmbedder. Vanilla
            // SentencePiece vocabs that don't train bos/eos as symbols (e.g. many T5/mT5
            // models) report -1 for these and are correctly left untouched.
            _needsFairseqRemap = HasFairseqFingerprint(spTok);
            // Changing ModelId here (not in the constructor) means any app with an
            // already-ingested vector store built under the old, wrong ids gets a loud
            // EmbeddingMismatchException on next startup instead of silently mixing two
            // incompatible vector spaces — reuses the guard vector stores already have.
            if (_needsFairseqRemap) ModelId += ":fseq";
        }
        // EmbedBatchAsync can overlap several Run() calls at once (see there) — a session
        // built with ORT's defaults ALSO parallelizes internally across every core for a
        // SINGLE Run() call, so N concurrent calls each spawning up to P internal threads
        // oversubscribes a P-core machine by a factor of N. Measured against a real 618K-char
        // legal document (232 chunks, avg ~2700 chars) on a 12-core machine: unbounded
        // concurrency (12 calls x up to 12 threads each) didn't finish an ingest in 5+ minutes;
        // splitting the core budget 4 ways (4 concurrent calls x 3 threads each, no
        // oversubscription) finished in 752s; fully sequential (1 call at a time, each free to
        // use every core) finished the SAME document in 455s — ~40% faster than the split.
        // BGE-M3's own ops parallelize well within one call, and concurrent calls mostly just
        // add cross-call cache/memory-bandwidth contention instead of real overlap, so
        // sequential wins here. (Forcing single-threaded Run() globally, IntraOpNumThreads=1
        // with unbounded batch concurrency, was also tried and measured ~3x SLOWER per call
        // than ORT's default — the fix is sequencing calls, not starving each one of threads.)
        _batchConcurrency = Math.Max(1, _maxBatchConcurrency ?? 1);
        var sessionOptions = new SessionOptions { IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / _batchConcurrency) };
        _session = new InferenceSession(_modelPath, sessionOptions);
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

    /// <summary>
    /// Whether <paramref name="tokenizer"/>'s raw vocabulary has the "fairseq"
    /// XLM-RoBERTa fingerprint: unk=0, bos=1, eos=2 in the raw <c>.model</c> file,
    /// which HuggingFace `transformers` remaps to &lt;s&gt;=0, &lt;pad&gt;=1, &lt;/s&gt;=2, &lt;unk&gt;=3
    /// (+1 to every real token) when training/exporting these models — confirmed
    /// against multilingual-e5-small's and BGE-M3's own <c>config.json</c> and with
    /// Python's <c>sentencepiece</c>. Vanilla SentencePiece vocabs that never train
    /// bos/eos as symbols (many T5/mT5 models) report -1 for those and correctly
    /// don't match. Internal, not private, so it's unit-testable without needing a
    /// full ONNX session.
    /// </summary>
    internal static bool HasFairseqFingerprint(SentencePieceTokenizer tokenizer) =>
        tokenizer.UnknownId == 0 && tokenizer.BeginningOfSentenceId == 1 && tokenizer.EndOfSentenceId == 2;

    /// <summary>
    /// Applies the fairseq remap described in <see cref="HasFairseqFingerprint"/>.
    /// <paramref name="rawIds"/> is exactly what <see cref="CreateSentencePiece"/>'s
    /// wrapping produces: [raw bos, ...content..., raw eos], always at least 2
    /// elements since both flags are always on. Internal, not private, so it's
    /// unit-testable directly against hand-picked id sequences.
    /// </summary>
    internal static long[] ApplyFairseqRemap(IReadOnlyList<int> rawIds)
    {
        var mapped = new long[rawIds.Count];
        mapped[0] = 0; // <s> — the wrapper CreateSentencePiece added via addBeginningOfSentence
        for (int i = 1; i < rawIds.Count - 1; i++)
            mapped[i] = rawIds[i] == 0 ? 3 : rawIds[i] + 1; // raw unk id is 0 for this vocab family (checked in InitializeAsync)
        mapped[rawIds.Count - 1] = 2; // </s> — the wrapper added via addEndOfSentence
        return mapped;
    }

    /// <summary>Embeds a query (a question). RagClient only ever calls this single-item
    /// overload for the user's question — see <see cref="EmbedBatchAsync"/> for passages.</summary>
    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) =>
        Task.Run(() => EmbedCore(_useQueryPassagePrefix ? "query: " + text : text), ct);

    /// <summary>Batch: inference is thread-safe, so run chunks across cores. RagClient only
    /// ever calls this for chunks being indexed (passages) — see <see cref="EmbedAsync"/> for queries.
    /// Bounded by <see cref="_batchConcurrency"/> (a fraction of the cores, the rest given to each
    /// Run() call's own internal threading — see InitializeAsync) instead of one-chunk-per-core,
    /// which oversubscribes the machine when Run() is itself multi-threaded (the common case).</summary>
    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var result = new float[texts.Count][];
        await Parallel.ForAsync(0, texts.Count,
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = _batchConcurrency },
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
        var rawIds = _tokenizer.EncodeToIds(text);
        var idValues = _needsFairseqRemap ? ApplyFairseqRemap(rawIds) : rawIds.Select(x => (long)x).ToArray();
        int n = Math.Max(1, idValues.Length);
        var idArr = new long[n];
        var mask = new long[n];
        var types = new long[n];
        for (int i = 0; i < idValues.Length; i++) { idArr[i] = idValues[i]; mask[i] = 1; }

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
