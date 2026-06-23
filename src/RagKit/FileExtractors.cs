namespace RagKit;

/// <summary>
/// Pluggable text extraction by file extension. Connector packages (e.g.
/// RagKit.Extractors for PDF/DOCX) register extractors; anything unregistered
/// falls back to reading the file as plain text. Keeps the core dependency-free.
/// </summary>
public static class FileExtractors
{
    private static readonly Dictionary<string, Func<string, string>> Registry = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Register an extractor for an extension (e.g. ".pdf").</summary>
    public static void Register(string extension, Func<string, string> extractor)
        => Registry[extension.StartsWith('.') ? extension : "." + extension] = extractor;

    /// <summary>Extract text from a file, using a registered extractor or plain-text fallback.</summary>
    public static string Extract(string path)
    {
        var ext = Path.GetExtension(path);
        return Registry.TryGetValue(ext, out var extractor) ? extractor(path) : File.ReadAllText(path);
    }
}
