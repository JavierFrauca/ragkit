namespace RagKit;

/// <summary>
/// Pluggable text extraction by file extension. Connector packages (e.g.
/// RagKit.Extractors for PDF/DOCX) register extractors; anything unregistered
/// falls back to reading the file as plain text. Keeps the core dependency-free.
/// </summary>
public static class FileExtractors
{
    private static readonly Dictionary<string, Func<string, string>> Registry = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Extensions the plain-text fallback treats as safe to read as text — used
    /// by <see cref="IsSupported"/> to skip binaries when scanning a folder (see
    /// <see cref="RagClient.IngestFolderAsync"/>); <see cref="Extract"/> itself doesn't
    /// filter, so a single-file call still reads any unregistered extension as text.</summary>
    private static readonly HashSet<string> PlainTextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".markdown", ".csv", ".json", ".xml", ".html", ".htm", ".log", ".yaml", ".yml" };

    /// <summary>Register an extractor for an extension (e.g. ".pdf").</summary>
    public static void Register(string extension, Func<string, string> extractor)
        => Registry[extension.StartsWith('.') ? extension : "." + extension] = extractor;

    /// <summary>Extract text from a file, using a registered extractor or plain-text fallback.</summary>
    public static string Extract(string path)
    {
        var ext = Path.GetExtension(path);
        return Registry.TryGetValue(ext, out var extractor) ? extractor(path) : File.ReadAllText(path);
    }

    /// <summary>Whether <paramref name="path"/>'s extension has a registered extractor
    /// (e.g. PDF/DOCX via RagKit.Extractors) or is a known plain-text format.</summary>
    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path);
        return Registry.ContainsKey(ext) || PlainTextExtensions.Contains(ext);
    }
}
