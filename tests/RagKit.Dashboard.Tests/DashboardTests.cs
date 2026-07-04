using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RagKit;

namespace RagKit.Dashboard.Tests;

/// <summary>A chat client that returns a fixed response — enough to build a
/// working RagClient for these host-level tests (no LLM behavior is exercised).</summary>
sealed class FakeChat(string response) : IChatClient
{
    public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
        => Task.FromResult(response);
}

public class DashboardTests
{
    private static async Task<RagClient> BuildRagAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ragkit-dashboard-test-" + Guid.NewGuid().ToString("N"));
        var store = new InMemoryVectorStore(dir);
        var embedder = new LocalEmbedder();
        var rag = new RagClient(new RagOptions(), embedder, store, new FakeChat("ok"), new FakeChat("{}"));
        await store.InitializeAsync(embedder.ModelId, embedder.Dimension);
        return rag;
    }

    private static async Task<(WebApplication App, HttpClient Client)> BuildHostAsync(RagClient rag, string? path = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(rag);
        var app = builder.Build();
        if (path is null) app.MapRagDashboard();
        else app.MapRagDashboard(path);
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    [Fact]
    public async Task MapRagDashboard_serves_the_embedded_index_page()
    {
        var (app, client) = await BuildHostAsync(await BuildRagAsync());
        await using var _ = app;

        var resp = await client.GetAsync("/rag-admin/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("RagKit", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MapRagDashboard_honors_a_custom_mount_path()
    {
        var (app, client) = await BuildHostAsync(await BuildRagAsync(), path: "/admin/rag");
        await using var _ = app;

        var resp = await client.GetAsync("/admin/rag/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Api_stats_resolves_RagClient_from_DI_and_reports_chunk_count()
    {
        var rag = await BuildRagAsync();
        await rag.DefineDomainAsync("docs");
        await rag.IngestAsync("contenido", "d.txt", domain: "docs");

        var (app, client) = await BuildHostAsync(rag);
        await using var _ = app;

        var resp = await client.GetAsync("/rag-admin/api/stats");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("\"chunkCount\":1", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unknown_asset_returns_404()
    {
        var (app, client) = await BuildHostAsync(await BuildRagAsync());
        await using var _ = app;

        var resp = await client.GetAsync("/rag-admin/does-not-exist.js");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public void MapRagDashboard_returns_a_builder_that_supports_RequireAuthorization()
    {
        // Auth-hook smoke test: MapRagDashboard must return something the consumer
        // can chain .RequireAuthorization() onto to wire their own auth — the
        // dashboard implements none of its own. Doesn't exercise a real auth
        // handler; just proves the extensibility point compiles and doesn't throw.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();

        var convention = app.MapRagDashboard();
        var ex = Record.Exception(() => convention.RequireAuthorization());
        Assert.Null(ex);
    }
}
