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

    /// <summary>
    /// The zero-config "real" embedder: downloads a small sentence-transformers ONNX
    /// model into a local cache the first time (then reused offline) and returns an
    /// <see cref="EmbedderConfig"/> ready to assign to <c>RagOptions.Embedder</c>.
    /// Semantic and private (runs in-process), unlike the non-semantic core fallback.
    /// <para>For multilingual corpora, point <c>EmbedderConfig.Model</c> at a model of
    /// your choice instead: any export with <c>model.onnx</c> plus either a WordPiece
    /// <c>vocab.txt</c> or a SentencePiece model (<c>sentencepiece.bpe.model</c>/
    /// <c>spiece.model</c>/<c>tokenizer.model</c>), e.g. multilingual-e5-small.</para>
    /// </summary>
    /// <param name="cacheDir">Where to cache the model. Default: per-user local app data.</param>
    public static async Task<EmbedderConfig> UseDefaultModelAsync(string? cacheDir = null, CancellationToken ct = default)
    {
        Enable();
        cacheDir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RagKit", "models", DefaultModelName);
        Directory.CreateDirectory(cacheDir);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        foreach (var (url, file) in DefaultModelFiles)
        {
            var dest = Path.Combine(cacheDir, file);
            if (File.Exists(dest) && new FileInfo(dest).Length > 0) continue;
            await DownloadAsync(http, url, dest, ct).ConfigureAwait(false);
        }
        return new EmbedderConfig { Kind = EmbedderKind.Onnx, Model = cacheDir };
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
    private InferenceSession? _session;
    private Tokenizer? _tokenizer;
    private bool _needsTokenTypes;

    public OnnxEmbedder(string modelDirOrPath)
    {
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
        Dimension = EmbedCore("warmup").Length;
    }, ct);

    private static Tokenizer CreateSentencePiece(string path)
    {
        using var fs = File.OpenRead(path);
        return SentencePieceTokenizer.Create(fs, addBeginningOfSentence: true, addEndOfSentence: true, specialTokens: null);
    }

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) => Task.Run(() => EmbedCore(text), ct);

    /// <summary>Batch: inference is thread-safe, so run chunks across cores.</summary>
    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var result = new float[texts.Count][];
        await Parallel.ForAsync(0, texts.Count,
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
            (i, _) => { result[i] = EmbedCore(texts[i]); return ValueTask.CompletedTask; }).ConfigureAwait(false);
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
        var output = results.First().AsTensor<float>(); // [1, seq, hidden]
        int seq = output.Dimensions[1];
        int hidden = output.Dimensions[2];
        var vec = new float[hidden];
        int valid = 0;
        for (int i = 0; i < seq; i++)
        {
            if (mask[i] == 0) continue;
            valid++;
            for (int d = 0; d < hidden; d++) vec[d] += output[0, i, d];
        }
        if (valid > 0) for (int d = 0; d < hidden; d++) vec[d] /= valid;

        double norm = 0;
        foreach (var x in vec) norm += (double)x * x;
        norm = Math.Sqrt(norm);
        if (norm > 0) for (int d = 0; d < hidden; d++) vec[d] = (float)(vec[d] / norm);
        return vec;
    }

    public void Dispose() => _session?.Dispose();
}
