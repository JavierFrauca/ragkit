using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RagKit.Internal;

/// <summary>
/// Tier-2 helpers producing short summaries used as embedding context — never
/// persisted/cited: a 3-line document summary (used when <see
/// cref="RagOptions.EnableContextualEmbedding"/> is on) and an atomic-table
/// explanation (used when a table chunk exceeds the embedder's <see
/// cref="IEmbedder.MaxChunkChars"/> and can't be split — tables are always atomic).
/// Reuses the same tier-2 <see cref="IChatClient"/> already injected into
/// <see cref="RagClient"/> for <see cref="Classifier"/>/<see cref="QueryRouter"/>/
/// <see cref="Guardrail"/>.
/// </summary>
internal sealed class Summarizer
{
    private const int ExcerptChars = 6000;

    private readonly IChatClient _llm;
    private readonly ILogger _log;

    public Summarizer(IChatClient llm, ILogger? logger = null) { _llm = llm; _log = logger ?? NullLogger.Instance; }

    public async Task<string> SummarizeDocumentAsync(string fullText, CancellationToken ct)
    {
        var excerpt = Excerpt(fullText);
        var messages = new[]
        {
            new ChatMessage("system",
                "Resume el siguiente documento en exactamente 3 líneas, en su mismo idioma. " +
                "Responde SOLO con el resumen, sin preámbulos ni numeración."),
            new ChatMessage("user", excerpt),
        };
        var summary = (await _llm.CompleteAsync(messages, ct).ConfigureAwait(false)).Trim();
        return summary;
    }

    public async Task<string> SummarizeTableAsync(string tableMarkdown, CancellationToken ct)
    {
        var excerpt = Excerpt(tableMarkdown);
        var messages = new[]
        {
            new ChatMessage("system",
                "Describe brevemente (2-4 líneas) qué representa la siguiente tabla Markdown: " +
                "qué columnas tiene y qué contiene cada fila, en el mismo idioma de la tabla. " +
                "Responde SOLO con la descripción."),
            new ChatMessage("user", excerpt),
        };
        var explanation = (await _llm.CompleteAsync(messages, ct).ConfigureAwait(false)).Trim();
        return explanation;
    }

    private static string Excerpt(string text) => text.Length > ExcerptChars ? text[..ExcerptChars] : text;
}
