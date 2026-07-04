using System.Net;
using System.Net.Http.Json;

namespace RagKit.Dashboard.Tests;

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
}
