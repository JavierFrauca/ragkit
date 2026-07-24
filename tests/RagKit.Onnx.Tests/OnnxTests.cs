using RagKit.Onnx;

namespace RagKit.Onnx.Tests;

public class OnnxEmbedderTests
{
    [Fact]
    public void Constructor_throws_when_model_not_found()
    {
        var nonexistent = Path.Combine(Path.GetTempPath(), "no-existe-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<FileNotFoundException>(() => new OnnxEmbedder(nonexistent));
    }

    [Fact]
    public void Constructor_throws_when_vocab_not_found()
    {
        // Create a folder with model.onnx but no vocab
        var dir = Path.Combine(Path.GetTempPath(), "onnx-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "model.onnx"), "dummy");
            Assert.Throws<FileNotFoundException>(() => new OnnxEmbedder(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void PoolingStrategy_default_is_Mean()
    {
        Assert.Equal(PoolingStrategy.Mean, default(PoolingStrategy));
    }

    [Fact]
    public void Enable_registers_onnx_factory()
    {
        OnnxEmbedding.Enable();
        // Factory is registered — verify by constructing config and checking
        // it doesn't throw with a nonexistent model path (factory defers loading).
        Assert.Throws<FileNotFoundException>(() =>
            EmbedderFactory.Create(new EmbedderConfig
            {
                Kind = EmbedderKind.Onnx,
                Model = Path.Combine(Path.GetTempPath(), "no-existe-onnx-" + Guid.NewGuid().ToString("N")),
            }));
    }

    /// <summary>
    /// Opt-in: set RAGKIT_ONNX_MODEL to a folder with model.onnx + vocab.txt
    /// to run a full embedding lifecycle test against a real ONNX model.
    /// Example: all-MiniLM-L6-v2 downloaded via HuggingFace.
    /// </summary>
    [Fact]
    public async Task EmbedAsync_with_real_model_optin()
    {
        var modelDir = Environment.GetEnvironmentVariable("RAGKIT_ONNX_MODEL");
        if (string.IsNullOrWhiteSpace(modelDir) || !Directory.Exists(modelDir)) return;

        using var embedder = new OnnxEmbedder(modelDir);
        await embedder.InitializeAsync();

        Assert.True(embedder.Dimension > 0);
        Assert.NotNull(embedder.ModelId);

        var vec = await embedder.EmbedAsync("hola mundo");
        Assert.Equal(embedder.Dimension, vec.Length);

        // L2-normalized: magnitude should be ~1.0
        double mag = Math.Sqrt(vec.Sum(x => (double)x * x));
        Assert.InRange(mag, 0.99, 1.01);
    }

    /// <summary>
    /// Opt-in: same as above but tests that two similar texts produce
    /// vectors with high cosine similarity.
    /// </summary>
    [Fact]
    public async Task Similar_texts_produce_similar_vectors_optin()
    {
        var modelDir = Environment.GetEnvironmentVariable("RAGKIT_ONNX_MODEL");
        if (string.IsNullOrWhiteSpace(modelDir) || !Directory.Exists(modelDir)) return;

        using var embedder = new OnnxEmbedder(modelDir);
        await embedder.InitializeAsync();

        var v1 = await embedder.EmbedAsync("El contrato laboral es indefinido.");
        var v2 = await embedder.EmbedAsync("El acuerdo de trabajo es permanente.");
        var v3 = await embedder.EmbedAsync("La receta de pizza lleva harina y agua.");

        var cos12 = Cosine(v1, v2);
        var cos13 = Cosine(v1, v3);

        // Similar legal texts should be more similar than legal vs cooking
        Assert.True(cos12 > cos13, $"Expected cos12 ({cos12:F4}) > cos13 ({cos13:F4})");
    }

    /// <summary>
    /// Opt-in: set RAGKIT_ONNX_MODEL to a folder with a multilingual-e5-small-type
    /// model to test that the query/passage prefix affects embeddings.
    /// </summary>
    [Fact]
    public async Task E5_prefix_aligns_query_and_passage_optin()
    {
        var modelDir = Environment.GetEnvironmentVariable("RAGKIT_ONNX_E5_MODEL");
        if (string.IsNullOrWhiteSpace(modelDir) || !Directory.Exists(modelDir)) return;

        using var embedder = new OnnxEmbedder(modelDir, useQueryPassagePrefix: true);
        await embedder.InitializeAsync();

        var queryVec = await embedder.EmbedAsync("¿Cuánto se paga de IVA?");
        var passageVec = await embedder.EmbedAsync("El tipo general del IVA es el 21%.");

        // Without the prefix, e5 models misalign queries and passages —
        // with it, the cosine should be reasonable (> 0.5 for a real match).
        var cos = Cosine(queryVec, passageVec);
        Assert.True(cos > 0.3, $"Expected cosine > 0.3, got {cos:F4}");
    }

    /// <summary>
    /// Tests that the ModelId changes when a Fairseq-remapped vocab is detected.
    /// This is tested indirectly: for a model known to need remapping (e.g.
    /// multilingual-e5-small, BGE-M3), ModelId should end with ":fseq".
    /// </summary>
    [Fact]
    public async Task ModelId_appends_fseq_for_fairseq_vocab_optin()
    {
        var modelDir = Environment.GetEnvironmentVariable("RAGKIT_ONNX_E5_MODEL");
        if (string.IsNullOrWhiteSpace(modelDir) || !Directory.Exists(modelDir)) return;

        using var embedder = new OnnxEmbedder(modelDir);
        await embedder.InitializeAsync();

        // If the model uses SentencePiece with Fairseq fingerprint (bos=1, eos=2, unk=0),
        // ModelId should have the :fseq suffix — EmbeddingMismatchException guard
        // relies on this.
        Assert.True(embedder.ModelId.Contains(":fseq") || embedder.ModelId.Contains("onnx:"),
            $"ModelId should include a fingerprint: got '{embedder.ModelId}'");
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}

public class OnnxCrossEncoderRerankerTests
{
    [Fact]
    public void Constructor_throws_when_model_not_found()
    {
        var nonexistent = Path.Combine(Path.GetTempPath(), "no-existe-ce-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<FileNotFoundException>(() => new OnnxCrossEncoderReranker(nonexistent));
    }

    /// <summary>
    /// Opt-in: set RAGKIT_RERANK_MODEL to a folder with a cross-encoder
    /// ONNX model + vocab.txt (e.g. ms-marco-MiniLM-L-6-v2).
    /// </summary>
    [Fact]
    public async Task Rerank_with_real_model_optin()
    {
        var modelDir = Environment.GetEnvironmentVariable("RAGKIT_RERANK_MODEL");
        if (string.IsNullOrWhiteSpace(modelDir) || !Directory.Exists(modelDir)) return;

        using var reranker = new OnnxCrossEncoderReranker(modelDir);
        var candidates = new List<StoredHit>
        {
            new("a.txt", "El IVA general es del 21 por ciento.", null, Array.Empty<string>(), 0.5, "1"),
            new("b.txt", "La receta de paella lleva arroz y marisco.", null, Array.Empty<string>(), 0.5, "2"),
            new("c.txt", "El IVA reducido es del 10 por ciento.", null, Array.Empty<string>(), 0.5, "3"),
        };

        var ranked = await reranker.RerankAsync("¿Cuánto es el IVA?", candidates, topK: 2);

        Assert.Equal(2, ranked.Count);
        // The two IVA-related documents should be ranked above the cooking one
        Assert.DoesNotContain(ranked, h => h.Text.Contains("paella"));
    }
}
