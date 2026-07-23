using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RagKit.Internal;

/// <summary>
/// Uses the tier-2 model to route a document: given the known domains and label
/// vocabulary (with descriptions) and an excerpt of the document, it picks the
/// best domain and the applicable labels. Returns JSON, parsed leniently and
/// validated against the known vocabulary (the model can't invent names).
/// </summary>
internal sealed class Classifier
{
    private readonly IChatClient _llm;
    private readonly ILogger _log;

    public Classifier(IChatClient llm, ILogger? logger = null) { _llm = llm; _log = logger ?? NullLogger.Instance; }

    public async Task<(string? Domain, List<string> Labels, double Confidence)> ClassifyAsync(
        string excerpt,
        IReadOnlyList<DomainInfo> domains,
        IReadOnlyList<LabelInfo> labels,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Dominios disponibles:");
        foreach (var d in domains) sb.AppendLine($"- {d.Name}: {d.Description}");
        if (labels.Count > 0)
        {
            sb.AppendLine("Etiquetas disponibles:");
            foreach (var l in labels) sb.AppendLine($"- {l.Name}: {l.Description}");
        }
        sb.AppendLine().AppendLine("Documento (extracto):").AppendLine(excerpt);

        var messages = new[]
        {
            new ChatMessage("system",
                "Clasificas documentos. Resume mentalmente y decide a qué dominio pertenece. " +
                "Responde SOLO con JSON: {\"domain\":\"<nombre>\",\"labels\":[\"<nombre>\",...],\"confidence\":<0..1>}. " +
                "Usa ÚNICAMENTE nombres de las listas. `confidence` es lo seguro que estás de la pertenencia al dominio; " +
                "si el documento no encaja en ningún dominio, devuelve domain=null y confidence baja."),
            new ChatMessage("user", sb.ToString()),
        };

        var raw = await _llm.CompleteAsync(messages, ct).ConfigureAwait(false);
        return Parse(raw, domains, labels);
    }

    /// <summary>Extract and validate the JSON the model returned (domain/labels against the
    /// known vocabulary; confidence in [0,1]).</summary>
    internal static (string? Domain, List<string> Labels, double Confidence) Parse(
        string raw, IReadOnlyList<DomainInfo> domains, IReadOnlyList<LabelInfo> labels)
    {
        var json = ExtractObject(raw);
        if (json is null) return (null, new List<string>(), 0.0);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? domain = null;
            if (root.TryGetProperty("domain", out var d) && d.ValueKind == JsonValueKind.String)
            {
                var name = d.GetString();
                domain = domains.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))?.Name;
            }

            var chosen = new List<string>();
            if (root.TryGetProperty("labels", out var ls) && ls.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in ls.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.String) continue;
                    var match = labels.FirstOrDefault(x => string.Equals(x.Name, el.GetString(), StringComparison.OrdinalIgnoreCase));
                    if (match is not null && !chosen.Contains(match.Name)) chosen.Add(match.Name);
                }
            }

            double confidence = 0.0;
            if (root.TryGetProperty("confidence", out var cf) && cf.ValueKind == JsonValueKind.Number)
                confidence = Math.Clamp(cf.GetDouble(), 0.0, 1.0);

            return (domain, chosen, confidence);
        }
        catch (JsonException)
        {
            return (null, new List<string>(), 0.0);
        }
    }

    /// <summary>Pull the first {...} block out of a possibly-chatty response.</summary>
    private static string? ExtractObject(string s)
    {
        int start = s.IndexOf('{');
        int end = s.LastIndexOf('}');
        return start >= 0 && end > start ? s.Substring(start, end - start + 1) : null;
    }
}
