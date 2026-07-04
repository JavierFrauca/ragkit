using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace RagKit.Dashboard.Tests;

public class IngestEndpointsTests
{
    [Fact]
    public async Task Ingest_streams_one_event_per_file_then_a_final_completed_event()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ragkit-dashboard-ingest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        File.WriteAllText(Path.Combine(tmp, "a.txt"), "contenido del fichero a");
        File.WriteAllText(Path.Combine(tmp, "b.txt"), "contenido del fichero b");

        var rag = await TestSupport.BuildRagAsync();
        await rag.DefineDomainAsync("docs");
        var (app, client) = await TestSupport.BuildHostAsync(rag);
        await using var _ = app;

        var start = await client.PostAsJsonAsync("/rag-admin/api/ingest", new { path = tmp, domain = "docs", recursive = true });
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var runId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("runId").GetString();

        var stream = await client.GetAsync($"/rag-admin/api/ingest/{runId}/stream");
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        var events = TestSupport.ParseSseEvents(await stream.Content.ReadAsStringAsync());

        var resultEvents = events.Where(e => e.TryGetProperty("result", out var ignored)).ToList();
        Assert.Equal(2, resultEvents.Count);
        Assert.Contains(resultEvents, e => e.GetProperty("result").GetProperty("source").GetString() == "a.txt");
        Assert.Contains(resultEvents, e => e.GetProperty("result").GetProperty("source").GetString() == "b.txt");

        var final = events.Last();
        Assert.True(final.GetProperty("done").GetBoolean());
        Assert.Equal("Completed", final.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Ingest_rejects_a_path_that_does_not_exist_on_the_server()
    {
        var rag = await TestSupport.BuildRagAsync();
        var (app, client) = await TestSupport.BuildHostAsync(rag);
        await using var _ = app;

        var start = await client.PostAsJsonAsync("/rag-admin/api/ingest", new { path = @"C:\no-existe-de-verdad-" + Guid.NewGuid(), domain = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, start.StatusCode);
    }

    [Fact]
    public async Task Streaming_an_unknown_run_id_returns_404()
    {
        var rag = await TestSupport.BuildRagAsync();
        var (app, client) = await TestSupport.BuildHostAsync(rag);
        await using var _ = app;

        var resp = await client.GetAsync($"/rag-admin/api/ingest/{Guid.NewGuid()}/stream");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
