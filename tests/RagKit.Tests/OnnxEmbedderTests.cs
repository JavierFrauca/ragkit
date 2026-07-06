using Microsoft.ML.Tokenizers;
using RagKit.Onnx;
using Xunit;

namespace RagKit.Tests;

/// <summary>
/// Regression coverage for the XLM-RoBERTa "fairseq" id-remap fix: RagKit.Onnx used
/// to feed raw SentencePiece ids straight to models (multilingual-e5-small, BGE-M3)
/// that actually expect HuggingFace's remapped ids, silently corrupting every
/// embedding — worse for CLS-pooling (BGE-M3) than mean-pooling (e5-small). No
/// network/model download needed: <see cref="OnnxEmbedder.ApplyFairseqRemap"/> is a
/// pure function, and the fingerprint detection is tested against two tiny
/// hand-trained SentencePiece models (a few hundred KB, committed under Fixtures/)
/// instead of the real multi-hundred-MB presets.
/// </summary>
public class OnnxEmbedderTests
{
    [Fact]
    public void ApplyFairseqRemap_shifts_real_tokens_by_one()
    {
        // [rawBos, "hola", "mundo", rawEos] — a plain sentence with two real tokens.
        var rawIds = new[] { 1, 10, 25, 2 };

        var mapped = OnnxEmbedder.ApplyFairseqRemap(rawIds);

        Assert.Equal(new long[] { 0, 11, 26, 2 }, mapped);
    }

    [Fact]
    public void ApplyFairseqRemap_maps_raw_unknown_id_to_fairseq_unk()
    {
        // A raw <unk> (id 0) in the middle of the sequence must become fairseq's <unk>=3,
        // not 0+1=1 (which would collide with <pad>'s position in the model's table).
        var rawIds = new[] { 1, 10, 0, 25, 2 };

        var mapped = OnnxEmbedder.ApplyFairseqRemap(rawIds);

        Assert.Equal(new long[] { 0, 11, 3, 26, 2 }, mapped);
    }

    [Fact]
    public void ApplyFairseqRemap_handles_the_minimum_bos_eos_only_sequence()
    {
        var rawIds = new[] { 1, 2 }; // empty content, just the wrapper

        var mapped = OnnxEmbedder.ApplyFairseqRemap(rawIds);

        Assert.Equal(new long[] { 0, 2 }, mapped);
    }

    [Fact]
    public void HasFairseqFingerprint_detects_xlm_roberta_style_vocab()
    {
        // unk=0, bos=1, eos=2 — the exact layout multilingual-e5-small and BGE-M3 use.
        var tokenizer = LoadFixture("fseq-fixture.model");

        Assert.True(OnnxEmbedder.HasFairseqFingerprint(tokenizer));
    }

    [Fact]
    public void HasFairseqFingerprint_does_not_flag_a_non_xlm_roberta_vocab()
    {
        // unk=0, but bos/eos swapped relative to the XLM-R layout (bos=2, eos=1) — a
        // vocab that must NOT get the fairseq remap; applying it would corrupt a model
        // that never needed it. (A model with no bos/eos at all, e.g. many T5/mT5
        // exports, can't even be loaded here with addBeginningOfSentence: true —
        // Microsoft.ML.Tokenizers itself requires a real bos/eos id for that; this
        // fixture instead covers "has bos/eos, just not this specific fingerprint".)
        var tokenizer = LoadFixture("vanilla-fixture.model");

        Assert.False(OnnxEmbedder.HasFairseqFingerprint(tokenizer));
    }

    private static SentencePieceTokenizer LoadFixture(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        using var stream = File.OpenRead(path);
        return SentencePieceTokenizer.Create(stream, addBeginningOfSentence: true, addEndOfSentence: true, specialTokens: null);
    }
}
