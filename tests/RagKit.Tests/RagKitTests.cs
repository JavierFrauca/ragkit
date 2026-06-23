using System.Net.Http;
using RagKit;
using RagKit.Internal;
using RagKit.Extractors;
using RagKit.Mcp;
using RagKit.Onnx;
using RagKit.Postgres;
using RagKit.SqlServer;
using Xunit;

namespace RagKit.Tests;

/// <summary>A chat client that returns a fixed response and records what it received.</summary>
sealed class FakeChat(string response) : IChatClient
{
    public IReadOnlyList<ChatMessage>? Last;
    public int Calls;

    public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
    {
        Last = messages;
        Calls++;
        return Task.FromResult(response);
    }
}

/// <summary>A tool-capable fake: first turn calls the search tool, then answers.</summary>
sealed class FakeAgentChat : IChatClient
{
    private int _turn;
    public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
        => Task.FromResult("plain");
    public bool SupportsTools => true;
    public Task<AgentTurn> NextAsync(IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools, CancellationToken ct = default)
    {
        _turn++;
        if (_turn == 1)
            return Task.FromResult(new AgentTurn(null, new[]
            {
                new ToolCall("call_1", "search_knowledge_base", "{\"query\":\"contrato\"}")
            }));
        return Task.FromResult(new AgentTurn("respuesta final basada en búsqueda", Array.Empty<ToolCall>()));
    }
}

/// <summary>A fake MCP connection exposing one "echo" tool.</summary>
sealed class FakeMcp : IMcpConnection
{
    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<McpToolInfo>>(new[]
        {
            new McpToolInfo("echo", "repite el texto", "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}}}")
        });
    public Task<string> CallToolAsync(string name, string argumentsJson, CancellationToken ct = default)
        => Task.FromResult($"echo:{argumentsJson}");
    public void Dispose() { }
}

/// <summary>An HTTP handler that returns 503 for the first N calls, then 200.</summary>
sealed class FlakyHandler(int failures) : System.Net.Http.HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        var code = Calls <= failures ? System.Net.HttpStatusCode.ServiceUnavailable : System.Net.HttpStatusCode.OK;
        return Task.FromResult(new System.Net.Http.HttpResponseMessage(code)
        {
            Content = new System.Net.Http.StringContent("ok")
        });
    }
}

/// <summary>An HTTP handler that returns a fixed SSE body with 200 OK.</summary>
sealed class SseHandler(string body) : System.Net.Http.HttpMessageHandler
{
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent(body)
        });
}

/// <summary>A reranker that moves a given source to the front.</summary>
sealed class FrontReranker(string front) : IReranker
{
    public Task<IReadOnlyList<StoredHit>> RerankAsync(string query, IReadOnlyList<StoredHit> candidates, int topK, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<StoredHit>>(
            candidates.OrderByDescending(h => h.Source == front).Take(topK).ToList());
}

public class RagKitTests
{
    private static async Task<RagClient> BuildAsync(IChatClient answer, FakeChat classifier, RagOptions? opts = null)
    {
        opts ??= new RagOptions();
        var dir = Path.Combine(Path.GetTempPath(), "ragkit-test-" + Guid.NewGuid().ToString("N"));
        var store = new InMemoryVectorStore(dir);
        var embedder = new LocalEmbedder();
        var rag = new RagClient(opts, embedder, store, answer, classifier);
        await store.InitializeAsync(embedder.ModelId, embedder.Dimension);
        return rag;
    }

    [Fact]
    public async Task Ingest_then_ask_grounds_on_the_relevant_document()
    {
        var answer = new FakeChat("respuesta simulada");
        var rag = await BuildAsync(answer, new FakeChat("{}"), new RagOptions { TopK = 3 });
        await rag.DefineDomainAsync("docs");

        await rag.IngestAsync("El contrato laboral indefinido regula el empleo fijo.", "laboral.txt", domain: "docs");
        await rag.IngestAsync("La receta de pizza con piña lleva masa y queso.", "cocina.txt", domain: "docs");

        var ans = await rag.AskAsync("contrato laboral", domain: "docs");
        Assert.Equal("respuesta simulada", ans.Answer);
        Assert.Equal("laboral.txt", ans.Citations[0].Source);
        Assert.Contains("Pregunta: contrato laboral", answer.Last!.Last().Content);
    }

    [Fact]
    public async Task No_domains_defined_rejects_ingest()
    {
        var rag = await BuildAsync(new FakeChat("x"), new FakeChat("{}"));
        var res = await rag.IngestAsync("texto", "d.txt");
        Assert.True(res.Rejected);
        Assert.Contains("dominios", res.Reason);
    }

    [Fact]
    public async Task Tier2_auto_classifies_above_threshold_and_rejects_below()
    {
        var answer = new FakeChat("respuesta");
        var classifier = new FakeChat("{\"domain\":\"fiscal\",\"labels\":[\"iva\"],\"confidence\":0.95}");
        var rag = await BuildAsync(answer, classifier, new RagOptions { ClassificationThreshold = 0.8 });
        await rag.DefineDomainAsync("fiscal", "impuestos");
        await rag.DefineDomainAsync("rrhh", "personal");
        await rag.DefineLabelAsync("iva");

        var ok = await rag.IngestAsync("El tipo general del IVA es 21%.", "iva.txt");
        Assert.False(ok.Rejected);
        Assert.Equal("fiscal", ok.Domain);
        Assert.Contains("iva", ok.Labels);
        Assert.Equal(1, classifier.Calls);   // tier-2 classified
        Assert.Equal(0, answer.Calls);        // tier-1 untouched on ingest

        // Now a low-confidence classification → rejected for non-correspondence.
        var low = new FakeChat("{\"domain\":\"fiscal\",\"labels\":[],\"confidence\":0.4}");
        var rag2 = await BuildAsync(answer, low, new RagOptions { ClassificationThreshold = 0.8 });
        await rag2.DefineDomainAsync("fiscal", "impuestos");
        var rej = await rag2.IngestAsync("Texto irrelevante sobre jardinería.", "x.txt");
        Assert.True(rej.Rejected);
        Assert.Contains("confianza", rej.Reason);
    }

    [Fact]
    public void Classifier_parse_validates_and_reads_confidence()
    {
        var domains = new[] { new DomainInfo("fiscal"), new DomainInfo("rrhh") };
        var labels = new[] { new LabelInfo("iva") };
        var (domain, chosen, conf) = Classifier.Parse(
            "ok: {\"domain\":\"fiscal\",\"labels\":[\"iva\",\"inventada\"],\"confidence\":0.9}", domains, labels);
        Assert.Equal("fiscal", domain);
        Assert.Equal(new[] { "iva" }, chosen);
        Assert.Equal(0.9, conf, 3);
    }

    [Fact]
    public async Task Store_guard_rejects_changing_embedding()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ragkit-guard-" + Guid.NewGuid().ToString("N"));
        await new InMemoryVectorStore(dir).InitializeAsync("model-A", 256);
        await Assert.ThrowsAsync<EmbeddingMismatchException>(
            () => new InMemoryVectorStore(dir).InitializeAsync("model-B", 256));
    }

    [Fact]
    public void Chunker_breaks_on_sentence_boundary()
        => Assert.Equal("Primera frase corta.",
            Chunker.Chunk("Primera frase corta. Segunda parte aqui.", size: 24, overlap: 4)[0]);

    [Fact]
    public async Task Agent_loop_calls_internal_search_then_answers()
    {
        var rag = await BuildAsync(new FakeAgentChat(), new FakeChat("{}"));
        await rag.DefineDomainAsync("docs");
        await rag.IngestAsync("el contrato laboral indefinido regula el empleo fijo", "laboral.txt", domain: "docs");

        var ans = await rag.AskAgentAsync("contrato", "docs");

        Assert.Equal("respuesta final basada en búsqueda", ans.Answer);
        Assert.NotEmpty(ans.Citations);           // the search tool surfaced the chunk
        Assert.Equal("laboral.txt", ans.Citations[0].Source);
    }

    [Fact]
    public async Task Agent_falls_back_to_oneshot_without_tool_support()
    {
        var rag = await BuildAsync(new FakeChat("one-shot answer"), new FakeChat("{}"));
        await rag.DefineDomainAsync("docs");
        await rag.IngestAsync("texto", "d.txt", domain: "docs");

        var ans = await rag.AskAgentAsync("q", "docs");  // FakeChat.SupportsTools == false
        Assert.Equal("one-shot answer", ans.Answer);
    }

    [Fact]
    public async Task Mcp_tools_adapt_to_rag_tools()
    {
        IMcpConnection conn = new FakeMcp();
        var tools = await McpTools.FromConnectionAsync(conn);
        Assert.Single(tools);
        Assert.Equal("echo", tools[0].Name);
        Assert.Contains("hola", await tools[0].InvokeAsync("{\"text\":\"hola\"}"));
    }

    // Opt-in end-to-end test against a real MCP server. It launches an external
    // process, so it only runs when you point it at your own server via env vars
    // (it never downloads/executes anything by default):
    //   RAGKIT_MCP_CMD=npx  RAGKIT_MCP_ARGS=-y;@modelcontextprotocol/server-everything;stdio
    [Fact]
    public async Task Mcp_stdio_connects_to_a_real_server_optin()
    {
        var cmd = Environment.GetEnvironmentVariable("RAGKIT_MCP_CMD");
        if (string.IsNullOrWhiteSpace(cmd)) return; // skipped unless explicitly configured
        var args = (Environment.GetEnvironmentVariable("RAGKIT_MCP_ARGS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);

        using var conn = await McpTools.ConnectStdioAsync(cmd, args);
        var tools = await conn.ListToolsAsync();
        Assert.NotEmpty(tools);
    }

    // --- Real LLM integration (opt-in; set RAGKIT_LLM_KEY; never commit a key) ---

    private static (string Url, string Key, string Model)? RealLlm()
    {
        var key = Environment.GetEnvironmentVariable("RAGKIT_LLM_KEY");
        if (string.IsNullOrWhiteSpace(key)) return null;
        return (Environment.GetEnvironmentVariable("RAGKIT_LLM_URL") ?? "https://api.deepseek.com",
                key, Environment.GetEnvironmentVariable("RAGKIT_LLM_MODEL") ?? "deepseek-chat");
    }

    private static RagOptions RealOpts(bool autoClassify) =>
        RealLlm() is var llm && llm is not null
            ? new RagOptions
            {
                Answer = new LlmConfig { Url = llm.Value.Url, ApiKey = llm.Value.Key, Model = llm.Value.Model },
                Classifier = new LlmConfig { Url = llm.Value.Url, ApiKey = llm.Value.Key, Model = llm.Value.Model },
                Store = new StoreConfig { Kind = VectorStoreKind.InMemory, DataPath = Path.Combine(Path.GetTempPath(), "ragkit-llm-" + Guid.NewGuid().ToString("N")) },
                Embedder = new EmbedderConfig { Kind = EmbedderKind.Local },
                AutoClassify = autoClassify,
            }
            : null!;

    [Fact]
    public async Task Real_llm_oneshot_grounds_answer()
    {
        if (RealLlm() is null) return; // skip without a key
        var rag = await RagClient.CreateAsync(RealOpts(autoClassify: false));
        await rag.DefineDomainAsync("docs");
        await rag.IngestAsync("El código interno del proyecto Zeta es QX-9981.", "zeta.txt", domain: "docs");

        var ans = await rag.AskAsync("¿Cuál es el código interno del proyecto Zeta?", domain: "docs");
        Assert.Contains("QX-9981", ans.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(ans.Citations);
    }

    [Fact]
    public async Task Real_llm_tier2_classifies()
    {
        if (RealLlm() is null) return;
        var rag = await RagClient.CreateAsync(RealOpts(autoClassify: true));
        await rag.DefineDomainAsync("fiscal", "impuestos, IVA, IRPF, tributos");
        await rag.DefineDomainAsync("rrhh", "personal, nóminas, contratos laborales");
        await rag.DefineLabelAsync("iva");

        var res = await rag.IngestAsync("El tipo general del IVA en España es del 21% desde 2012.", "iva.txt");
        Assert.False(res.Rejected, res.Reason);
        Assert.Equal("fiscal", res.Domain);
    }

    [Fact]
    public async Task Real_llm_agent_loop_uses_search_tool()
    {
        if (RealLlm() is null) return;
        var rag = await RagClient.CreateAsync(RealOpts(autoClassify: false));
        await rag.DefineDomainAsync("docs");
        await rag.IngestAsync("La clave de activación del módulo Orion es ORN-7744-XK.", "orion.txt", domain: "docs");

        // The model can't know this invented fact — it must call search_knowledge_base.
        var ans = await rag.AskAgentAsync("¿Cuál es la clave de activación del módulo Orion?", domain: "docs");
        Assert.Contains("ORN-7744-XK", ans.Answer, StringComparison.OrdinalIgnoreCase);
    }

    // Opt-in: validate an OpenAI-compatible embeddings endpoint (e.g. Ollama +
    // nomic-embed-text). Set RAGKIT_EMBED_URL and RAGKIT_EMBED_MODEL.
    [Fact]
    public async Task Api_embedder_when_available()
    {
        var url = Environment.GetEnvironmentVariable("RAGKIT_EMBED_URL");
        var model = Environment.GetEnvironmentVariable("RAGKIT_EMBED_MODEL");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(model)) return;

        var emb = new ApiEmbedder(new EmbedderConfig
        {
            Kind = EmbedderKind.OpenAi, Url = url, Model = model,
            ApiKey = Environment.GetEnvironmentVariable("RAGKIT_EMBED_KEY"),
        });
        Assert.True(emb.Dimension > 0);

        var a = await emb.EmbedAsync("the employment contract");
        var b = await emb.EmbedAsync("a work agreement");
        var c = await emb.EmbedAsync("pineapple pizza");
        static double Cos(float[] x, float[] y) { double s = 0; for (int i = 0; i < x.Length; i++) s += x[i] * y[i]; return s; }
        Assert.True(Cos(a, b) > Cos(a, c));
    }

    [Fact]
    public async Task HttpRetry_retries_transient_then_succeeds()
    {
        var handler = new FlakyHandler(failures: 2);
        using var http = new System.Net.Http.HttpClient(handler) { BaseAddress = new Uri("http://x/") };
        using var resp = await HttpRetry.PostAsync(http, "p",
            () => new System.Net.Http.StringContent("{}"), default, attempts: 3);
        Assert.True(resp.IsSuccessStatusCode);
        Assert.Equal(3, handler.Calls); // 2 transient 503s + 1 success
    }

    [Fact]
    public async Task OpenAi_streaming_concatenates_sse_deltas()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hola\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\" mundo\"}}]}\n\n" +
            ": keep-alive\n\n" +
            "data: [DONE]\n\n";
        using var http = new System.Net.Http.HttpClient(new SseHandler(sse)) { BaseAddress = new Uri("http://x/") };
        var client = new OpenAiChatClient("http://x", "", "m", http);
        var sb = new System.Text.StringBuilder();
        await foreach (var piece in client.StreamAsync(new[] { new ChatMessage("user", "hi") }))
            sb.Append(piece);
        Assert.Equal("Hola mundo", sb.ToString());
    }

    [Fact]
    public async Task Default_streaming_yields_completion_as_one_chunk()
    {
        IChatClient chat = new FakeChat("respuesta única");
        var pieces = new List<string>();
        await foreach (var p in chat.StreamAsync(new[] { new ChatMessage("user", "x") }))
            pieces.Add(p);
        Assert.Equal(new[] { "respuesta única" }, pieces);
    }

    [Fact]
    public void Bm25_ranks_document_with_query_term_first()
    {
        var lex = new LexicalIndex();
        lex.Add(new StoredChunk("a.txt", "el contrato laboral indefinido regula el empleo", null, Array.Empty<string>()));
        lex.Add(new StoredChunk("b.txt", "la receta de pizza con piña y queso", null, Array.Empty<string>()));
        lex.Add(new StoredChunk("c.txt", "disposiciones generales del convenio colectivo", null, Array.Empty<string>()));
        var hits = lex.Search("contrato laboral", 3, null, null);
        Assert.NotEmpty(hits);
        Assert.Equal("a.txt", hits[0].Chunk.Source);
    }

    [Fact]
    public async Task Hybrid_retrieval_finds_literal_term()
    {
        var rag = await BuildAsync(new FakeChat("ok"), new FakeChat("{}"), new RagOptions { Hybrid = true, TopK = 3 });
        await rag.DefineDomainAsync("docs");
        await rag.IngestAsync("el artículo 14 regula las vacaciones retribuidas", "art14.txt", domain: "docs");
        await rag.IngestAsync("disposiciones generales del convenio colectivo", "gen.txt", domain: "docs");

        var ans = await rag.AskAsync("artículo 14", domain: "docs");
        Assert.Equal("art14.txt", ans.Citations[0].Source);
    }

    [Fact]
    public async Task Reranker_is_applied_after_fusion()
    {
        var rag = await BuildAsync(new FakeChat("ok"), new FakeChat("{}"), new RagOptions { Hybrid = true, TopK = 3 });
        await rag.DefineDomainAsync("docs");
        await rag.IngestAsync("contrato laboral uno", "a.txt", domain: "docs");
        await rag.IngestAsync("contrato laboral dos", "b.txt", domain: "docs");
        rag.SetReranker(new FrontReranker("b.txt"));

        var ans = await rag.AskAsync("contrato", domain: "docs");
        Assert.Equal("b.txt", ans.Citations[0].Source);
    }

    [Fact]
    public void Extractors_read_pdf_and_docx()
    {
        DocumentExtractors.Enable();
        var tmp = Path.Combine(Path.GetTempPath(), "ragkit-ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);

        // PDF built with PdfPig, then extracted via the registry.
        var pdfPath = Path.Combine(tmp, "d.pdf");
        var b = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var font = b.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        var pg = b.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        pg.AddText("contrato laboral", 12, new UglyToad.PdfPig.Core.PdfPoint(25, 700), font);
        File.WriteAllBytes(pdfPath, b.Build());
        Assert.Contains("contrato", FileExtractors.Extract(pdfPath));

        // DOCX built with OpenXml, then extracted via the registry.
        var docxPath = Path.Combine(tmp, "d.docx");
        using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(
            docxPath, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(
                new DocumentFormat.OpenXml.Wordprocessing.Body(
                    new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                        new DocumentFormat.OpenXml.Wordprocessing.Run(
                            new DocumentFormat.OpenXml.Wordprocessing.Text("contrato laboral docx")))));
            main.Document.Save();
        }
        Assert.Contains("contrato", FileExtractors.Extract(docxPath));
    }

    [Fact]
    public async Task Onnx_embedder_produces_semantic_vectors_when_model_present()
    {
        var dir = Environment.GetEnvironmentVariable("RAGKIT_ONNX_MODEL");
        if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "model.onnx"))) return; // skip if model absent

        using var emb = new OnnxEmbedder(dir);
        Assert.Equal(384, emb.Dimension);
        Assert.StartsWith("onnx:", emb.ModelId);

        var a = await emb.EmbedAsync("the indefinite employment contract");
        var b = await emb.EmbedAsync("a permanent work contract");   // related
        var c = await emb.EmbedAsync("a pineapple pizza recipe");    // unrelated

        static double Cos(float[] x, float[] y) { double s = 0; for (int i = 0; i < x.Length; i++) s += x[i] * y[i]; return s; }
        Assert.True(Cos(a, b) > Cos(a, c), "frases relacionadas deben ser más similares que las no relacionadas");
    }

    [Fact]
    public async Task Postgres_store_roundtrip_when_available()
    {
        const string cs = "Host=127.0.0.1;Port=5432;Username=postgres;Password=ragkit;Database=ragkit;Timeout=3";
        var coll = "ragkit_test_" + Guid.NewGuid().ToString("N")[..8];
        var store = new PostgresVectorStore(cs, coll);
        var emb = new LocalEmbedder();
        try { await store.InitializeAsync(emb.ModelId, emb.Dimension); }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException) { return; } // skip if Postgres not up

        await store.CreateDomainAsync("fiscal", "impuestos");
        Assert.Contains(await store.ListDomainsAsync(), d => d.Name == "fiscal");

        await store.AddChunkAsync("doc.txt", "el IVA general es del 21%", "fiscal", new[] { "iva" },
            await emb.EmbedAsync("el IVA general es del 21%"));
        var hits = await store.SearchAsync(await emb.EmbedAsync("IVA"), 5, "fiscal");
        Assert.NotEmpty(hits);
        Assert.Equal("fiscal", hits[0].Domain);
        Assert.True(await store.CountAsync() >= 1);

        // Label filter + guard.
        Assert.NotEmpty(await store.SearchAsync(await emb.EmbedAsync("IVA"), 5, "fiscal", new[] { "iva" }));
        await Assert.ThrowsAsync<EmbeddingMismatchException>(
            () => new PostgresVectorStore(cs, coll).InitializeAsync("otro", 128));
    }

    [Fact]
    public async Task SqlServer_store_roundtrip_when_available()
    {
        const string cs = "Server=127.0.0.1,1433;User Id=sa;Password=Ragkit2025!Strong;Database=ragkit;TrustServerCertificate=True;Connect Timeout=5";
        var coll = "ragkit_test_" + Guid.NewGuid().ToString("N")[..8];
        var store = new SqlServerVectorStore(cs, coll);
        var emb = new LocalEmbedder();
        try { await store.InitializeAsync(emb.ModelId, emb.Dimension); }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException) { return; } // skip if SQL Server not up

        await store.CreateDomainAsync("fiscal", "impuestos");
        Assert.Contains(await store.ListDomainsAsync(), d => d.Name == "fiscal");

        await store.AddChunkAsync("doc.txt", "el IVA general es del 21%", "fiscal", new[] { "iva" },
            await emb.EmbedAsync("el IVA general es del 21%"));
        var hits = await store.SearchAsync(await emb.EmbedAsync("IVA"), 5, "fiscal");
        Assert.NotEmpty(hits);
        Assert.Equal("fiscal", hits[0].Domain);
        Assert.NotEmpty(await store.SearchAsync(await emb.EmbedAsync("IVA"), 5, "fiscal", new[] { "iva" }));

        await Assert.ThrowsAsync<EmbeddingMismatchException>(
            () => new SqlServerVectorStore(cs, coll).InitializeAsync("otro", 128));
    }

    [Fact]
    public async Task Qdrant_store_roundtrip_when_available()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        bool up;
        try { up = (await http.GetAsync("http://127.0.0.1:6333/readyz")).IsSuccessStatusCode; }
        catch { up = false; }
        if (!up) return; // integration test: skipped when Qdrant isn't running

        var coll = "ragkit_test_" + Guid.NewGuid().ToString("N")[..8];
        var store = new QdrantVectorStore("http://127.0.0.1:6333", coll);
        var emb = new LocalEmbedder();
        await store.InitializeAsync(emb.ModelId, emb.Dimension);
        await store.CreateDomainAsync("fiscal", "impuestos");

        Assert.Contains(await store.ListDomainsAsync(), d => d.Name == "fiscal");

        await store.AddChunkAsync("doc.txt", "el IVA general es del 21%", "fiscal", new[] { "iva" },
            await emb.EmbedAsync("el IVA general es del 21%"));
        var hits = await store.SearchAsync(await emb.EmbedAsync("IVA"), 5, "fiscal");
        Assert.NotEmpty(hits);
        Assert.Equal("fiscal", hits[0].Domain);

        // Guard: reopening the same collection with a different dimension must throw.
        await Assert.ThrowsAsync<EmbeddingMismatchException>(
            () => new QdrantVectorStore("http://127.0.0.1:6333", coll).InitializeAsync("other", 128));
    }
}
