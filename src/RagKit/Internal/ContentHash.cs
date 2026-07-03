using System.Security.Cryptography;
using System.Text;

namespace RagKit.Internal;

/// <summary>Stable content hash used to detect unchanged documents on re-ingestion
/// (see <see cref="RagClient.IngestIfChangedAsync"/>).</summary>
internal static class ContentHash
{
    public static string Compute(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
