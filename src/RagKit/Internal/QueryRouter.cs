using System.Text;
using System.Text.Json;

namespace RagKit.Internal;

/// <summary>
/// Query-time sibling of <see cref="Classifier"/>: instead of routing a document
/// on ingest, it uses the tier-2 model to route a user <em>question</em> into the
/// best domain and, if defined, one or more profiles. Returns JSON, parsed
/// leniently and validated against the known domains/profiles (the model can't
/// invent names). Profiles must belong to the chosen domain.
/// </summary>
internal sealed class QueryRouter
{
    private readonly IChatClient _llm;

    public QueryRouter(IChatClient llm) => _llm = llm;

    public async Task<RouteDecision> RouteAsync(
        string question,
        IReadOnlyList<DomainInfo> domains,
        IReadOnlyList<ProfileInfo> profiles,
        bool multiProfile,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Dominios disponibles:");
        foreach (var d in domains) sb.AppendLine($"- {d.Name}: {d.Description}");
        if (profiles.Count > 0)
        {
            sb.AppendLine("Perfiles disponibles (lente para enfocar la respuesta), por dominio:");
            foreach (var p in profiles) sb.AppendLine($"- [{p.Domain}] {p.Name}: {p.Description}");
        }
        sb.AppendLine().AppendLine("Pregunta del usuario:").AppendLine(question);

        var instruction =
            "Enrutas la pregunta de un usuario al dominio correcto y, si procede, a uno o más perfiles. " +
            "Responde SOLO con JSON: {\"domain\":\"<nombre|null>\",\"profiles\":[\"<nombre>\",...],\"confidence\":<0..1>}. " +
            "Usa ÚNICAMENTE nombres de las listas; los perfiles deben pertenecer al dominio elegido. " +
            (multiProfile
                ? "Puedes devolver varios perfiles si la pregunta cruza varios oficios/ramas. "
                : "Devuelve como mucho UN perfil, el más adecuado. ") +
            "`confidence` es lo seguro que estás del dominio; si no encaja en ninguno, domain=null y confidence baja.";

        var messages = new[]
        {
            new ChatMessage("system", instruction),
            new ChatMessage("user", sb.ToString()),
        };

        var raw = await _llm.CompleteAsync(messages, ct).ConfigureAwait(false);
        return Parse(raw, domains, profiles, multiProfile);
    }

    /// <summary>Extract and validate the JSON the model returned (domain/profiles against the
    /// known vocabulary; confidence in [0,1]). Labels are the union of the chosen profiles' labels.</summary>
    internal static RouteDecision Parse(
        string raw, IReadOnlyList<DomainInfo> domains, IReadOnlyList<ProfileInfo> profiles, bool multiProfile)
    {
        var json = ExtractObject(raw);
        if (json is null) return new RouteDecision(null, Array.Empty<string>(), Array.Empty<string>(), 0.0);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? domain = null;
            if (root.TryGetProperty("domain", out var d) && d.ValueKind == JsonValueKind.String)
                domain = domains.FirstOrDefault(x => string.Equals(x.Name, d.GetString(), StringComparison.OrdinalIgnoreCase))?.Name;

            var chosenProfiles = new List<string>();
            var labels = new List<string>();
            if (domain is not null && root.TryGetProperty("profiles", out var ps) && ps.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in ps.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.String) continue;
                    var match = profiles.FirstOrDefault(x =>
                        string.Equals(x.Domain, domain, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(x.Name, el.GetString(), StringComparison.OrdinalIgnoreCase));
                    if (match is null || chosenProfiles.Contains(match.Name)) continue;
                    chosenProfiles.Add(match.Name);
                    if (match.Labels is not null)
                        foreach (var l in match.Labels) if (!labels.Contains(l)) labels.Add(l);
                    if (!multiProfile) break;
                }
            }

            double confidence = 0.0;
            if (root.TryGetProperty("confidence", out var cf) && cf.ValueKind == JsonValueKind.Number)
                confidence = Math.Clamp(cf.GetDouble(), 0.0, 1.0);

            return new RouteDecision(domain, chosenProfiles, labels, confidence);
        }
        catch (JsonException)
        {
            return new RouteDecision(null, Array.Empty<string>(), Array.Empty<string>(), 0.0);
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
