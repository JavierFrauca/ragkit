using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using RagKit;

namespace RagKit.Onnx;

/// <summary>
/// Second-stage reranker backed by a local ONNX <b>cross-encoder</b> (e.g.
/// <c>ms-marco-MiniLM-L-6-v2</c>): it scores each (query, passage) pair jointly —
/// more accurate than the bi-encoder retrieval, at a per-candidate cost. Install it
/// with <see cref="RagClient.SetReranker"/>. The model folder needs <c>model.onnx</c>
/// and a WordPiece <c>vocab.txt</c>.
/// <para><b>Experimental</b>: targets single-logit cross-encoders (one relevance score
/// per pair). Verify against your model — outputs that aren't a single score won't rank
/// as expected.</para>
/// </summary>
public sealed class OnnxCrossEncoderReranker : IReranker, IDisposable
{
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly bool _needsTokenTypes;

    public OnnxCrossEncoderReranker(string modelDirOrPath)
    {
        var modelPath = File.Exists(modelDirOrPath) ? modelDirOrPath : Path.Combine(modelDirOrPath, "model.onnx");
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"No se encontró el cross-encoder ONNX en '{modelPath}'.");
        var dir = Path.GetDirectoryName(Path.GetFullPath(modelPath))!;
        var vocab = Path.Combine(dir, "vocab.txt");
        if (!File.Exists(vocab))
            throw new FileNotFoundException($"No se encontró vocab.txt junto al cross-encoder en '{dir}'.");

        _tokenizer = BertTokenizer.Create(vocab);
        _session = new InferenceSession(modelPath);
        _needsTokenTypes = _session.InputMetadata.ContainsKey("token_type_ids");
    }

    public Task<IReadOnlyList<StoredHit>> RerankAsync(
        string query, IReadOnlyList<StoredHit> candidates, int topK, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<StoredHit>>(() =>
            candidates
                .Select(h => h with { Score = Score(query, h.Text) })
                .OrderByDescending(h => h.Score)
                .Take(topK)
                .ToList(), ct);

    /// <summary>Run the cross-encoder on one (query, passage) pair → relevance score.</summary>
    private double Score(string query, string passage)
    {
        // BERT pair encoding: [CLS] query [SEP] passage [SEP], segment 0 then 1.
        var q = _tokenizer.EncodeToIds(query, addSpecialTokens: false);
        var p = _tokenizer.EncodeToIds(passage, addSpecialTokens: false);

        var ids = new List<long>(q.Count + p.Count + 3) { _tokenizer.ClassificationTokenId };
        foreach (var t in q) ids.Add(t);
        ids.Add(_tokenizer.SeparatorTokenId);
        int firstSegment = ids.Count;                 // [CLS] q [SEP] belongs to segment 0
        foreach (var t in p) ids.Add(t);
        ids.Add(_tokenizer.SeparatorTokenId);

        int n = ids.Count;
        var idArr = ids.ToArray();
        var mask = new long[n];
        Array.Fill(mask, 1L);
        var types = new long[n];
        for (int i = firstSegment; i < n; i++) types[i] = 1;

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(idArr, new[] { 1, n })),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, new[] { 1, n })),
        };
        if (_needsTokenTypes)
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(types, new[] { 1, n })));

        using var results = _session.Run(inputs);
        var flat = results.First().AsTensor<float>().ToArray();
        return flat.Length > 0 ? flat[0] : 0.0;       // single-logit relevance score
    }

    public void Dispose() => _session.Dispose();
}
