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

    public static int Int(this JsonElement e, string prop, int fallback)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : fallback;
}

/// <summary>`search_knowledge_base` — the internal MCP-style tool to query the vector store.
/// Accumulates the citations of everything it surfaced for the final answer. Locked to the
/// turn's routed <paramref name="domain"/> unless <paramref name="allowCrossDomain"/> is set
/// (<see cref="AgentToolScope.CrossDomainSearch"/>) — without it, any `domain` the model puts
/// in its own arguments is ignored, so a routed turn can never escape its domain.</summary>
internal sealed class SearchTool : IRagTool
{
    private readonly RagClient _rag;
    private readonly string? _domain;
    private readonly bool _allowCrossDomain;
    public List<Citation> Citations { get; } = new();

    public SearchTool(RagClient rag, string? domain, bool allowCrossDomain = false)
    {
        _rag = rag;
        _domain = domain;
        _allowCrossDomain = allowCrossDomain;
    }

    public string Name => "search_knowledge_base";
    public string Description => "Busca fragmentos relevantes en la base de conocimiento interna.";
    public string ParametersSchema => _allowCrossDomain
        ? """{"type":"object","properties":{"query":{"type":"string","description":"texto a buscar"},"domain":{"type":"string","description":"dominio donde buscar; omite para buscar en todos los dominios"},"labels":{"type":"array","items":{"type":"string"}}},"required":["query"]}"""
        : """{"type":"object","properties":{"query":{"type":"string","description":"texto a buscar"},"labels":{"type":"array","items":{"type":"string"}}},"required":["query"]}""";

    public async Task<string> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var a = Args.Parse(argumentsJson);
        var query = a.Str("query");
        var labels = a.StrArray("labels");
        // Without the flag, any "domain" the model puts in argumentsJson is ignored on
        // purpose — _domain (the routed one) is the safety floor for untrusted callers.
        // With the flag: an explicit domain narrows the search to it; omitting it searches
        // every domain (null reaches the store unfiltered) — the model's escape hatch out
        // of the routed domain when it decides the routing didn't pick the right one.
        var domain = _allowCrossDomain ? a.TryGetPropertyDomain() : _domain;
        var hits = await _rag.RetrieveAsync(query, domain, labels.Count > 0 ? labels : null, ct).ConfigureAwait(false);
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

/// <summary>`find_domains` — ranks already-defined domains by semantic similarity of their
/// `Description` to a natural-language query, using the same embedder as ingestion/search
/// (cosine via <see cref="RagKit.Internal.Vec.Dot"/> on the already-normalized vectors it
/// returns — the same assumption <see cref="Stores.InMemoryVectorStore"/> relies on for
/// ranking search hits). Meant for catalogs with enough domains that reading the full
/// <c>list_domains</c> output and picking by hand stops being the cheaper option; embeds
/// every description on each call rather than caching, so it scales with catalog size, not
/// with the number of calls (see README for the caching trade-off if this becomes hot).</summary>
internal sealed class FindDomainsTool(RagClient rag) : IRagTool
{
    public string Name => "find_domains";
    public string Description => "Rankea los dominios definidos por relevancia semántica de su descripción frente a una consulta.";
    public string ParametersSchema =>
        """{"type":"object","properties":{"query":{"type":"string"},"topK":{"type":"integer","description":"máximo de resultados (5 por defecto)"}},"required":["query"]}""";
    public async Task<string> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var a = Args.Parse(argumentsJson);
        var query = a.Str("query");
        if (string.IsNullOrWhiteSpace(query)) return "error: falta 'query'";
        var topK = Math.Max(1, a.Int("topK", 5));
        var domains = await rag.ListDomainsAsync(ct).ConfigureAwait(false);
        if (domains.Count == 0) return "(sin dominios)";
        var qv = await rag.Embed(query, ct).ConfigureAwait(false);
        var ranked = new List<(DomainInfo Domain, double Score)>(domains.Count);
        foreach (var d in domains)
        {
            var dv = await rag.Embed(string.IsNullOrWhiteSpace(d.Description) ? d.Name : d.Description, ct).ConfigureAwait(false);
            ranked.Add((d, RagKit.Internal.Vec.Dot(qv, dv)));
        }
        ranked.Sort((x, y) => y.Score.CompareTo(x.Score));
        return string.Join("\n", ranked.Take(topK).Select(r => $"- {r.Domain.Name} ({r.Score:0.00}): {r.Domain.Description}"));
    }
}

/// <summary>`find_labels` — same idea as <see cref="FindDomainsTool"/> but over
/// <see cref="LabelInfo.Description"/>, so the model can pick which labels to pass into
/// `search_knowledge_base`'s/`ingest_document`'s `labels` before filtering by them.</summary>
internal sealed class FindLabelsTool(RagClient rag) : IRagTool
{
    public string Name => "find_labels";
    public string Description => "Rankea las etiquetas definidas por relevancia semántica de su descripción frente a una consulta.";
    public string ParametersSchema =>
        """{"type":"object","properties":{"query":{"type":"string"},"topK":{"type":"integer","description":"máximo de resultados (5 por defecto)"}},"required":["query"]}""";
    public async Task<string> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var a = Args.Parse(argumentsJson);
        var query = a.Str("query");
        if (string.IsNullOrWhiteSpace(query)) return "error: falta 'query'";
        var topK = Math.Max(1, a.Int("topK", 5));
        var labels = await rag.ListLabelsAsync(ct).ConfigureAwait(false);
        if (labels.Count == 0) return "(sin etiquetas)";
        var qv = await rag.Embed(query, ct).ConfigureAwait(false);
        var ranked = new List<(LabelInfo Label, double Score)>(labels.Count);
        foreach (var l in labels)
        {
            var lv = await rag.Embed(string.IsNullOrWhiteSpace(l.Description) ? l.Name : l.Description, ct).ConfigureAwait(false);
            ranked.Add((l, RagKit.Internal.Vec.Dot(qv, lv)));
        }
        ranked.Sort((x, y) => y.Score.CompareTo(x.Score));
        return string.Join("\n", ranked.Take(topK).Select(r => $"- {r.Label.Name} ({r.Score:0.00}): {r.Label.Description}"));
    }
}

/// <summary>`get_document_chunks` — every chunk of one document, in ingestion order, via
/// the already-public <see cref="RagClient.ListChunksAsync"/> paginated in a loop rather
/// than a bespoke per-backend "by index" query — <see cref="IVectorStore"/> gains no new
/// member for this. Bounded by <see cref="MaxChunks"/> so a pathologically large document
/// can't blow up the model's context in one call; the result says how many were returned
/// vs. how many exist when it's capped.</summary>
internal sealed class GetDocumentChunksTool(RagClient rag) : IRagTool
{
    private const int PageSize = 50;
    // Pagination order is backend-specific and not guaranteed to correlate with ChunkIndex
    // (see GetAdjacentChunkTool), so "the first MaxShown chunks" has to mean first-by-index,
    // not first-encountered — which means fetching the whole document (up to FetchCap as a
    // safety net against a pathological one) before sorting and truncating for display.
    private const int FetchCap = 2000;
    private const int MaxShown = 200;

    public string Name => "get_document_chunks";
    public string Description => "Devuelve todos los chunks de un documento, en orden.";
    public string ParametersSchema =>
        """{"type":"object","properties":{"source":{"type":"string"},"domain":{"type":"string"}},"required":["source"]}""";

    public async Task<string> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var a = Args.Parse(argumentsJson);
        var source = a.Str("source");
        if (string.IsNullOrWhiteSpace(source)) return "error: falta 'source'";
        var domain = a.TryGetPropertyDomain();

        var chunks = new List<StoredChunk>();
        string? cursor = null;
        do
        {
            var page = await rag.ListChunksAsync(source, domain, PageSize, cursor, ct).ConfigureAwait(false);
            chunks.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null && chunks.Count < FetchCap);

        if (chunks.Count == 0) return $"error: no se encontraron chunks para '{source}'" + (domain is null ? "" : $" en el dominio '{domain}'");

        var ordered = chunks.OrderBy(c => c.ChunkIndex).Take(MaxShown).ToList();
        var sb = new StringBuilder();
        if (chunks.Count > ordered.Count) sb.AppendLine($"(mostrando los primeros {ordered.Count} de {chunks.Count} chunks)");
        foreach (var c in ordered) sb.AppendLine($"[{c.ChunkIndex}] (id={c.Id}) {c.Text}");
        return sb.ToString();
    }
}

/// <summary>`get_adjacent_chunk` — the chunk right before/after a given one within the same
/// document, keyed off <see cref="StoredChunk.ChunkIndex"/> (its ingestion order) rather than
/// a backend-native "next row" query, for the same reason as <see cref="GetDocumentChunksTool"/>:
/// no new <see cref="IVectorStore"/> member, just <see cref="RagClient.ListChunksAsync"/>
/// paginated and searched in memory. Meant to pull the rest of a passage that a search hit
/// or <c>get_document_chunks</c> cut off mid-thought.</summary>
internal sealed class GetAdjacentChunkTool(RagClient rag) : IRagTool
{
    private const int PageSize = 50;
    private const int MaxScanned = 500;

    public string Name => "get_adjacent_chunk";
    public string Description => "Devuelve el chunk siguiente o anterior a uno dado, dentro del mismo documento.";
    public string ParametersSchema =>
        """{"type":"object","properties":{"source":{"type":"string"},"chunkId":{"type":"string"},"direction":{"type":"string","enum":["next","previous"]},"domain":{"type":"string"}},"required":["source","chunkId","direction"]}""";

    public async Task<string> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var a = Args.Parse(argumentsJson);
        var source = a.Str("source");
        var chunkId = a.Str("chunkId");
        var direction = a.Str("direction");
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(chunkId)) return "error: faltan 'source'/'chunkId'";
        if (direction is not ("next" or "previous")) return "error: 'direction' debe ser 'next' o 'previous'";
        var domain = a.TryGetPropertyDomain();

        // Pagination order is backend-specific (a row id / point order), NOT guaranteed to
        // correlate with ChunkIndex — so the target and its neighbor can land on any page in
        // any order. No shortcut for "stop once the target is found": the whole document (up
        // to MaxScanned) has to be indexed by ChunkIndex first, then resolved from that map.
        StoredChunk? current = null;
        var byIndex = new Dictionary<int, StoredChunk>();
        string? cursor = null;
        do
        {
            var page = await rag.ListChunksAsync(source, domain, PageSize, cursor, ct).ConfigureAwait(false);
            foreach (var c in page.Items)
            {
                byIndex[c.ChunkIndex] = c;
                if (string.Equals(c.Id, chunkId, StringComparison.Ordinal)) current = c;
            }
            cursor = page.NextCursor;
        } while (cursor is not null && byIndex.Count < MaxScanned);

        if (current is null) return $"error: no se encontró el chunk '{chunkId}' en '{source}'";

        var wantIndex = current.ChunkIndex + (direction == "next" ? 1 : -1);
        if (byIndex.TryGetValue(wantIndex, out var adjacent))
            return $"[{adjacent.ChunkIndex}] (id={adjacent.Id}) {adjacent.Text}";

        return direction == "next" ? "(es el último chunk del documento)" : "(es el primer chunk del documento)";
    }
}

/// <summary>`get_document_summary` — the 3-line tier-2 summary generated for a source
/// during ingestion (see <see cref="RagOptions.EnableContextualEmbedding"/>). Read-only;
/// registered under <see cref="AgentToolScope.Classification"/> like the other list tools.</summary>
internal sealed class GetDocumentSummaryTool(RagClient rag) : IRagTool
{
    public string Name => "get_document_summary";
    public string Description => "Devuelve el resumen de 3 líneas generado para un documento durante su ingesta, si existe.";
    public string ParametersSchema =>
        """{"type":"object","properties":{"source":{"type":"string"}},"required":["source"]}""";
    public async Task<string> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var a = Args.Parse(argumentsJson);
        var source = a.Str("source");
        if (string.IsNullOrWhiteSpace(source)) return "error: falta 'source'";
        var summary = await rag.GetDocumentSummaryAsync(source, ct).ConfigureAwait(false);
        return summary ?? "(sin resumen para este documento)";
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

internal sealed class CreateProfileTool(RagClient rag) : IRagTool
{
    public string Name => "create_profile";
    public string Description => "Crea o actualiza un perfil (lente) dentro de un dominio: un prompt enfocado y, opcionalmente, etiquetas que acotan la búsqueda.";
    public string ParametersSchema =>
        """{"type":"object","properties":{"name":{"type":"string"},"domain":{"type":"string"},"description":{"type":"string"},"prompt":{"type":"string"},"labels":{"type":"array","items":{"type":"string"}}},"required":["name","domain"]}""";
    public async Task<string> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var a = Args.Parse(argumentsJson);
        var name = a.Str("name");
        var domain = a.Str("domain");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(domain)) return "error: faltan 'name'/'domain'";
        var prompt = a.Str("prompt");
        var labels = a.StrArray("labels");
        await rag.DefineProfileAsync(new ProfileInfo(name, domain, a.Str("description"),
            string.IsNullOrWhiteSpace(prompt) ? null : prompt, labels.Count > 0 ? labels : null), ct).ConfigureAwait(false);
        return $"perfil '{name}' en '{domain}' guardado";
    }
}

internal sealed class CreateGuardrailTool(RagClient rag) : IRagTool
{
    public string Name => "create_guardrail";
    public string Description => "Crea una regla de guardarail en lenguaje natural (entrada o salida), opcionalmente acotada a un dominio/perfil.";
    public string ParametersSchema =>
        """{"type":"object","properties":{"description":{"type":"string"},"stage":{"type":"string","enum":["input","output"]},"domain":{"type":"string"},"profile":{"type":"string"}},"required":["description"]}""";
    public async Task<string> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var a = Args.Parse(argumentsJson);
        var desc = a.Str("description");
        if (string.IsNullOrWhiteSpace(desc)) return "error: falta 'description'";
        var stage = a.Str("stage").Equals("output", StringComparison.OrdinalIgnoreCase) ? GuardrailStage.Output : GuardrailStage.Input;
        var domain = a.Str("domain");
        var profile = a.Str("profile");
        await rag.DefineGuardrailAsync(new GuardrailRule(desc, stage,
            string.IsNullOrWhiteSpace(domain) ? null : domain,
            string.IsNullOrWhiteSpace(profile) ? null : profile), ct).ConfigureAwait(false);
        return $"regla de guardarail ({stage}) guardada";
    }
}

internal static class JsonExt
{
    public static string? TryGetPropertyDomain(this JsonElement a)
        => a.TryGetProperty("domain", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
