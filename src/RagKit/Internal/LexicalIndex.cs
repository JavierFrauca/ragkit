using System.Text;

namespace RagKit.Internal;

/// <summary>
/// In-memory BM25 lexical index over chunk texts, used for the lexical half of
/// hybrid search (fused with vector results via RRF). Lives in RagKit so hybrid
/// works identically across every vector-store backend; rebuilt from the store at
/// startup and updated on ingest.
/// </summary>
internal sealed class LexicalIndex
{
    private sealed record Entry(StoredChunk Chunk, Dictionary<string, int> Tf, int Len);

    private readonly List<Entry> _entries = new();
    private readonly Dictionary<string, int> _df = new();
    private long _totalLen;
    private readonly object _lock = new();

    public void Add(StoredChunk chunk)
    {
        var tf = new Dictionary<string, int>();
        int len = 0;
        foreach (var t in Tokenize(chunk.Text)) { tf[t] = tf.GetValueOrDefault(t) + 1; len++; }
        lock (_lock)
        {
            _entries.Add(new Entry(chunk, tf, len));
            foreach (var t in tf.Keys) _df[t] = _df.GetValueOrDefault(t) + 1;
            _totalLen += len;
        }
    }

    /// <summary>Remove every entry indexed under <paramref name="source"/> (used after
    /// <c>IVectorStore.DeleteBySourceAsync</c> so hybrid search doesn't keep serving stale hits).</summary>
    public void RemoveBySource(string source) =>
        RemoveWhere(e => string.Equals(e.Chunk.Source, source, StringComparison.Ordinal));

    /// <summary>Remove every entry indexed under <paramref name="domain"/> (used after
    /// <c>IVectorStore.DeleteByDomainAsync</c> so hybrid search doesn't keep serving stale hits).</summary>
    public void RemoveByDomain(string domain) =>
        RemoveWhere(e => string.Equals(e.Chunk.Domain, domain, StringComparison.OrdinalIgnoreCase));

    private void RemoveWhere(Func<Entry, bool> predicate)
    {
        lock (_lock)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var e = _entries[i];
                if (!predicate(e)) continue;
                foreach (var t in e.Tf.Keys)
                {
                    if (!_df.TryGetValue(t, out var df)) continue;
                    if (df <= 1) _df.Remove(t); else _df[t] = df - 1;
                }
                _totalLen -= e.Len;
                _entries.RemoveAt(i);
            }
        }
    }

    public List<(StoredChunk Chunk, double Score)> Search(string query, int k, string? domain, IReadOnlyList<string>? labels)
    {
        var qterms = Tokenize(query).Distinct().ToList();
        var result = new List<(StoredChunk, double)>();
        if (qterms.Count == 0) return result;

        lock (_lock)
        {
            int n = _entries.Count;
            if (n == 0) return result;
            double avg = (double)_totalLen / n;
            const double k1 = 1.5, b = 0.75;

            foreach (var e in _entries)
            {
                if (domain != null && !string.Equals(e.Chunk.Domain, domain, StringComparison.OrdinalIgnoreCase)) continue;
                if (labels is { Count: > 0 } && !labels.All(l => e.Chunk.Labels.Contains(l, StringComparer.OrdinalIgnoreCase))) continue;

                double score = 0;
                foreach (var t in qterms)
                {
                    if (!e.Tf.TryGetValue(t, out var f)) continue;
                    int df = _df.GetValueOrDefault(t);
                    double idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
                    score += idf * (f * (k1 + 1)) / (f + k1 * (1 - b + b * e.Len / avg));
                }
                if (score > 0) result.Add((e.Chunk, score));
            }
        }
        result.Sort((a, b2) => b2.Item2.CompareTo(a.Item2));
        if (result.Count > k) result.RemoveRange(k, result.Count - k);
        return result;
    }

    private static IEnumerable<string> Tokenize(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (sb.Length > 0) { if (sb.Length >= 2) yield return sb.ToString(); sb.Clear(); }
        }
        if (sb.Length >= 2) yield return sb.ToString();
    }
}
