namespace RagKit;

/// <summary>
/// Pluggable text extraction by file extension. Connector packages (e.g.
/// RagKit.Extractors for PDF/DOCX) register extractors; anything unregistered
/// falls back to reading the file as plain text. Keeps the core dependency-free.
/// </summary>
public static class FileExtractors
{
    private static readonly Dictionary<string, Func<string, string>> Registry = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Func<string, CancellationToken, Task<string>>> AsyncRegistry = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Extensions the plain-text fallback treats as safe to read as text — used
    /// by <see cref="IsSupported"/> to skip binaries when scanning a folder (see
    /// <see cref="RagClient.IngestFolderAsync"/>); <see cref="Extract"/> itself doesn't
    /// filter, so a single-file call still reads any unregistered extension as text.</summary>
    private static readonly HashSet<string> PlainTextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".markdown", ".csv", ".json", ".xml", ".html", ".htm", ".log", ".yaml", ".yml" };

    /// <summary>Register a synchronous extractor for an extension (e.g. ".pdf"). Once
    /// started, a synchronous extractor always runs to completion — nothing can
    /// interrupt it mid-extraction, not even <see cref="ExtractAsync"/>'s <c>ct</c> (see
    /// its remarks). For an extractor that can genuinely be interrupted, register it via
    /// <see cref="Register(string, Func{string, CancellationToken, Task{string}})"/> instead
    /// (or in addition — the two registries are independent, so a connector can register
    /// both: the sync one for <see cref="Extract"/> callers, the async one for
    /// <see cref="ExtractAsync"/> callers).</summary>
    public static void Register(string extension, Func<string, string> extractor)
        => Registry[Normalize(extension)] = extractor;

    /// <summary>Register an async, cancellable extractor for an extension. Prefer this over
    /// the synchronous overload whenever the extraction can take a long time on adversarial
    /// or unusually large input (see e.g. RagKit.Markdown's <c>PdfToMarkdown.ConvertAsync</c>,
    /// which checks the token between pages) — it's the only registration kind
    /// <see cref="ExtractAsync"/> can actually interrupt mid-extraction, rather than merely
    /// stop waiting on.</summary>
    public static void Register(string extension, Func<string, CancellationToken, Task<string>> extractor)
        => AsyncRegistry[Normalize(extension)] = extractor;

    private static string Normalize(string extension) => extension.StartsWith('.') ? extension : "." + extension;

    /// <summary>Extract text from a file, using a registered extractor or plain-text fallback.
    /// Synchronous — a slow or hung extractor blocks the caller for however long it takes,
    /// with no way to give up. Prefer <see cref="ExtractAsync"/> for anything that must be
    /// boundable (a folder-ingestion worker, a request with a deadline, etc.).</summary>
    public static string Extract(string path)
    {
        var ext = Path.GetExtension(path);
        return Registry.TryGetValue(ext, out var extractor) ? extractor(path) : File.ReadAllText(path);
    }

    /// <summary>
    /// Async extraction, cancellable via <paramref name="ct"/> — prefer this over
    /// <see cref="Extract"/> for anything that needs to bound how long extraction can run.
    /// Resolution order: an async-registered extractor for the extension (genuinely
    /// interruptible — the extractor itself observes <paramref name="ct"/>, e.g. between
    /// pages of a PDF); else a sync-registered extractor, run on the thread pool — cancelling
    /// here only stops the CALLER from waiting further, since a plain
    /// <c>Func&lt;string,string&gt;</c> has no way to observe a token, so the abandoned call
    /// keeps running on its own thread until it finishes (or never does) on its own; else
    /// <see cref="File.ReadAllTextAsync(string, CancellationToken)"/> (genuinely cancellable
    /// I/O) for an unregistered extension.
    /// </summary>
    public static async Task<string> ExtractAsync(string path, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(path);
        if (AsyncRegistry.TryGetValue(ext, out var asyncExtractor))
            return await asyncExtractor(path, ct).ConfigureAwait(false);

        if (Registry.TryGetValue(ext, out var extractor))
        {
            var task = Task.Run(() => extractor(path), CancellationToken.None);
            var completed = await Task.WhenAny(task, Task.Delay(Timeout.InfiniteTimeSpan, ct)).ConfigureAwait(false);
            if (completed != task)
                throw new OperationCanceledException(ct);
            return await task.ConfigureAwait(false);
        }

        return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
    }

    /// <summary>Whether <paramref name="path"/>'s extension has a registered extractor
    /// (sync, async, or both — e.g. PDF/DOCX via RagKit.Extractors) or is a known
    /// plain-text format.</summary>
    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path);
        return Registry.ContainsKey(ext) || AsyncRegistry.ContainsKey(ext) || PlainTextExtensions.Contains(ext);
    }
}
