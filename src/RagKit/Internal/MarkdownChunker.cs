using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Microsoft.Extensions.Logging;
using RagKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace RagKit.Internal;

/// <summary>A chunk of Markdown ready to embed: raw text, the "H1 &gt; H2 &gt; H3"
/// heading breadcrumb it was extracted under, and whether it's an atomic table
/// (never split, regardless of size).</summary>
internal sealed record MarkdownChunk(string Text, string Breadcrumb, bool IsAtomicTable);

/// <summary>
/// Structural chunker: parses Markdown into an AST (Markdig), groups content under
/// each heading (carrying the heading breadcrumb as chunk metadata), and only
/// subdivides a section by paragraph — falling back to <see cref="Chunker"/>'s fixed
/// window for any single paragraph still too large — when it exceeds
/// <paramref name="maxChars"/>. Tables are always emitted as their own chunk,
/// regardless of size — see <see cref="MarkdownChunk.IsAtomicTable"/>.
/// </summary>
internal static class MarkdownChunker
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
    private static ILogger _log = NullLogger.Instance;

    public static void SetLogger(ILogger logger) => _log = logger;

    public static List<MarkdownChunk> Chunk(string markdown, int maxChars, int overlap = 200)
    {
        _log.LogDebug("Chunking markdown: chars={Len} max={Max} overlap={Overlap}", markdown.Length, maxChars, overlap);
        var result = new List<MarkdownChunk>();
        if (string.IsNullOrWhiteSpace(markdown)) return result;

        MarkdownDocument document;
        try { document = Markdown.Parse(markdown, Pipeline); }
        catch (Exception ex) { throw new ChunkingException("Markdig parse error", ex); }
        var headingStack = new List<(int Level, string Text)>();
        var sectionBuffer = new List<Block>();

        void FlushSection()
        {
            if (sectionBuffer.Count == 0) return;
            string breadcrumb = string.Join(" > ", headingStack.Select(h => h.Text));
            string text = SourceText(markdown, sectionBuffer[0].Span.Start, sectionBuffer[^1].Span.End);
            EmitSection(result, text, breadcrumb, maxChars, overlap);
            sectionBuffer.Clear();
        }

        foreach (var block in document)
        {
            if (block is HeadingBlock heading)
            {
                FlushSection();
                string headingText = SourceText(markdown, heading.Span.Start, heading.Span.End)
                    .TrimStart('#', ' ', '\t').TrimEnd();
                while (headingStack.Count > 0 && headingStack[^1].Level >= heading.Level)
                    headingStack.RemoveAt(headingStack.Count - 1);
                headingStack.Add((heading.Level, headingText));
                continue;
            }

            if (block is Table table)
            {
                FlushSection();
                string breadcrumb = string.Join(" > ", headingStack.Select(h => h.Text));
                string tableText = SourceText(markdown, table.Span.Start, table.Span.End);
                if (tableText.Length > 0)
                    result.Add(new MarkdownChunk(tableText, breadcrumb, IsAtomicTable: true));
                continue;
            }

            sectionBuffer.Add(block);
        }
        FlushSection();
        _log.LogDebug("Markdown chunked: {ChunkCount} chunks", result.Count);
        return result;
    }

    private static void EmitSection(List<MarkdownChunk> result, string text, string breadcrumb, int maxChars, int overlap)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (text.Length <= maxChars)
        {
            result.Add(new MarkdownChunk(text, breadcrumb, IsAtomicTable: false));
            return;
        }
        // Section too large for one chunk: split by paragraph, and fall back to the
        // fixed-window Chunker (sentence/whitespace-aware) for any single paragraph
        // that's still too big on its own.
        foreach (var paragraph in SplitParagraphs(text))
        {
            if (paragraph.Length <= maxChars)
            {
                result.Add(new MarkdownChunk(paragraph, breadcrumb, IsAtomicTable: false));
            }
            else
            {
                foreach (var piece in Chunker.Chunk(paragraph, maxChars, overlap))
                    result.Add(new MarkdownChunk(piece, breadcrumb, IsAtomicTable: false));
            }
        }
    }

    private static IEnumerable<string> SplitParagraphs(string text)
        => text.Replace("\r\n", "\n")
               .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
               .Select(p => p.Trim())
               .Where(p => p.Length > 0);

    private static string SourceText(string markdown, int start, int end)
    {
        start = Math.Max(0, Math.Min(start, markdown.Length));
        end = Math.Max(start - 1, Math.Min(end, markdown.Length - 1));
        return end < start ? "" : markdown.Substring(start, end - start + 1);
    }
}
