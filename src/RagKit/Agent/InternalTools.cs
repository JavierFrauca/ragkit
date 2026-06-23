using System.Text;
using System.Text.Json;

namespace RagKit.Agent;

/// <summary>Helpers for parsing tool arguments leniently.</summary>
internal static class Args
{
    public static JsonElement Parse(string json)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement.Clone(); }
        catch { return JsonDocument.Parse("{}").RootElement.Clone(); }
    }

    public static string Str(this JsonElement e, string prop, string fallback = "")
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    public static List<string> StrArray(this JsonElement e, string prop)
    {
        var list = new List<string>();
        if (e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var x in v.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String) list.Add(x.GetString()!);
        return list;
    }
}

/// <summary>`search_knowledge_base` — the internal MCP-style tool to query the vector store.
/// Accumulates the citations of everything it surfaced for the final answer.</summary>
internal sealed class SearchTool : IRagTool
{
    private readonly RagClient _rag;
    private readonly string? _domain;
    public List<Citation> Citations { get; } = new();

    public SearchTool(RagClient rag, string? domain) { _rag = rag; _domain = domain; }

    public string Name => "search_knowledge_base";
    public string Description => "Busca fragmentos relevantes en la base de conocimiento interna.";
    public string ParametersSchema =>
        """{"type":"object","properties":{"query":{"type":"string","description":"texto a buscar"},"labels":{"type":"array","items":{"type":"string"}}},"required":["query"]}""";

    public async Task<string> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var a = Args.Parse(argumentsJson);
        var query = a.Str("query");
        var labels = a.StrArray("labels");
        var hits = await _rag.RetrieveAsync(query, _domain, labels.Count > 0 ? labels : null, ct).ConfigureAwait(false);
        Citations.AddRange(RagClient.ToCitations(hits));
        if (hits.Count == 0) return "Sin resultados.";
        var sb = new StringBuilder();
        for (int i = 0; i < hits.Count; i++)
            sb.AppendLine($"[{i + 1}] ({hits[i].Source}) {hits[i].Text}");
        return sb.ToString();
    }
}

internal sealed class ListDomainsTool(RagClient rag) : IRagTool
{
    public string Name => "list_domains";
    public string Description => "Lista los dominios definidos y sus descripciones.";
    public string ParametersSchema => """{"type":"object","properties":{}}""";
    public async Task<string> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var ds = await rag.ListDomainsAsync(ct).ConfigureAwait(false);
        return ds.Count == 0 ? "(sin dominios)" : string.Join("\n", ds.Select(d => $"- {d.Name}: {d.Description}"));
    }
}

internal sealed class ListLabelsTool(RagClient rag) : IRagTool
{
    public string Name => "list_labels";
    public string Description => "Lista las etiquetas definidas.";
    public string ParametersSchema => """{"type":"object","properties":{}}""";
    public async Task<string> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var ls = await rag.ListLabelsAsync(ct).ConfigureAwait(false);
        return ls.Count == 0 ? "(sin etiquetas)" : string.Join("\n", ls.Select(l => l.Name));
    }
}

internal sealed class CreateDomainTool(RagClient rag) : IRagTool
{
    public string Name => "create_domain";
    public string Description => "Crea un dominio nuevo con su descripción.";
    public string ParametersSchema =>
        """{"type":"object","properties":{"name":{"type":"string"},"description":{"type":"string"}},"required":["name"]}""";
    public async Task<string> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var a = Args.Parse(argumentsJson);
        var name = a.Str("name");
        if (string.IsNullOrWhiteSpace(name)) return "error: falta 'name'";
        await rag.DefineDomainAsync(name, a.Str("description"), ct).ConfigureAwait(false);
        return $"dominio '{name}' creado";
    }
}

internal sealed class CreateLabelTool(RagClient rag) : IRagTool
{
    public string Name => "create_label";
    public string Description => "Crea una etiqueta nueva.";
    public string ParametersSchema =>
        """{"type":"object","properties":{"name":{"type":"string"},"description":{"type":"string"}},"required":["name"]}""";
    public async Task<string> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var a = Args.Parse(argumentsJson);
        var name = a.Str("name");
        if (string.IsNullOrWhiteSpace(name)) return "error: falta 'name'";
        await rag.DefineLabelAsync(name, a.Str("description"), ct).ConfigureAwait(false);
        return $"etiqueta '{name}' creada";
    }
}

internal sealed class IngestTool(RagClient rag, string? defaultDomain) : IRagTool
{
    public string Name => "ingest_document";
    public string Description => "Ingesta un documento en la base (lo clasifica si no se indica dominio).";
    public string ParametersSchema =>
        """{"type":"object","properties":{"text":{"type":"string"},"source":{"type":"string"},"domain":{"type":"string"}},"required":["text"]}""";
    public async Task<string> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var a = Args.Parse(argumentsJson);
        var text = a.Str("text");
        if (string.IsNullOrWhiteSpace(text)) return "error: falta 'text'";
        var domain = a.TryGetPropertyDomain() is { } d ? d : defaultDomain;
        var r = await rag.IngestAsync(text, a.Str("source", "doc"), domain, null, ct).ConfigureAwait(false);
        return r.Rejected ? $"rechazado: {r.Reason}" : $"ingestado en '{r.Domain}' ({r.ChunkCount} chunks)";
    }
}

internal static class JsonExt
{
    public static string? TryGetPropertyDomain(this JsonElement a)
        => a.TryGetProperty("domain", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
