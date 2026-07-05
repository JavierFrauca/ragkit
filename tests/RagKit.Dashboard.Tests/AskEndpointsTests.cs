using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using RagKit;

namespace RagKit.Dashboard.Tests;

/// <summary>A tool-capable fake used only to prove GET api/ask/agent/stream wires up
/// AskAgentStreamAsync correctly end-to-end over HTTP — the tool-calling loop itself
/// (ordering, guardrail buffering, fragmented SSE parsing) is covered in depth by
/// RagKit.Tests.</summary>
sealed class ToolCapableFakeChat : IChatClient
{
    private int _turn;
    public IReadOnlyList<ToolSpec>? LastTools;
    public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
        => Task.FromResult("plain");
    public bool SupportsTools => true;

    public async IAsyncEnumerable<AgentDelta> NextStreamAsync(
        IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        LastTools = tools;
        _turn++;
        if (_turn == 1)
        {
            yield return new AgentDelta(AgentDeltaKind.ToolCallStarted, ToolName: "search_knowledge_base");
            yield return new AgentDelta(AgentDeltaKind.ToolCallsReady, ToolCalls: new[]
            {
                new ToolCall("call_1", "search_knowledge_base", "{\"query\":\"contrato\"}"),
            });
            yield break;
        }
        yield return new AgentDelta(AgentDeltaKind.ContentPiece, Content: "ok");
    }
}

public class AskEndpointsTests
{
    [Fact]
    public async Task Ask_answers_without_streaming()
    {
        var rag = await TestSupport.BuildRagAsync();
        await rag.DefineDomainAsync("docs");
        await rag.IngestAsync("el contrato laboral indefinido regula el empleo", "laboral.txt", domain: "docs");

        var (app, client) = await TestSupport.BuildHostAsync(rag);
        await using var _ = app;

        var resp = await client.PostAsJsonAsync("/rag-admin/api/ask", new { question = "contrato laboral", domain = "docs", profile = (string?)null });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("ok", body.GetProperty("answer").GetString());
        Assert.Equal("laboral.txt", body.GetProperty("citations")[0].GetProperty("source").GetString());
    }

    [Fact]
    public async Task AskStream_sends_citations_before_tokens_then_a_final_done_event()
    {
        var rag = await TestSupport.BuildRagAsync();
        await rag.DefineDomainAsync("docs");
        await rag.IngestAsync("el contrato laboral indefinido regula el empleo", "laboral.txt", domain: "docs");

        var (app, client) = await TestSupport.BuildHostAsync(rag);
        await using var _ = app;

        var resp = await client.GetAsync("/rag-admin/api/ask/stream?question=contrato+laboral&domain=docs");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var events = TestSupport.ParseSseEvents(await resp.Content.ReadAsStringAsync());

        Assert.True(events.Count >= 3); // citations + at least one token + done
        Assert.Equal("laboral.txt", events[0].GetProperty("citations")[0].GetProperty("source").GetString());

        var tokenEvents = events.Skip(1).SkipLast(1).ToList();
        Assert.All(tokenEvents, e => Assert.True(e.TryGetProperty("token", out var ignored)));
        Assert.Equal("ok", string.Concat(tokenEvents.Select(e => e.GetProperty("token").GetString())));

        Assert.True(events.Last().GetProperty("done").GetBoolean());
    }

    [Fact]
    public async Task AskAgentStream_sends_tool_events_then_citations_then_tokens_then_done()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ragkit-dashboard-test-" + Guid.NewGuid().ToString("N"));
        var store = new InMemoryVectorStore(dir);
        var embedder = new LocalEmbedder();
        var chat = new ToolCapableFakeChat();
        var rag = new RagClient(new RagOptions(), embedder, store, chat, new FakeChat("{}"));
        await store.InitializeAsync(embedder.ModelId, embedder.Dimension);
        await rag.DefineDomainAsync("docs");
        await rag.IngestAsync("el contrato laboral indefinido regula el empleo", "laboral.txt", domain: "docs");

        var (app, client) = await TestSupport.BuildHostAsync(rag);
        await using var _ = app;

        var resp = await client.GetAsync("/rag-admin/api/ask/agent/stream?question=contrato&domain=docs");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var events = TestSupport.ParseSseEvents(await resp.Content.ReadAsStringAsync());

        Assert.Equal("ToolCallStarted", events[0].GetProperty("kind").GetString());
        Assert.Equal("search_knowledge_base", events[0].GetProperty("toolName").GetString());
        Assert.Equal("ToolCallFinished", events[1].GetProperty("kind").GetString());
        Assert.Equal("Citations", events[2].GetProperty("kind").GetString());
        Assert.Equal("laboral.txt", events[2].GetProperty("citations")[0].GetProperty("source").GetString());
        Assert.Equal("Token", events[3].GetProperty("kind").GetString());
        Assert.Equal("ok", events[3].GetProperty("token").GetString());
        Assert.True(events.Last().GetProperty("done").GetBoolean());

        // The dashboard has no auth of its own -- verified directly here: even
        // though AgentToolScope defaults to All, the endpoint hardcodes SearchOnly,
        // so no mutation tool is ever offered to the model.
        Assert.Equal(new[] { "search_knowledge_base" }, chat.LastTools!.Select(t => t.Name));
    }
}
