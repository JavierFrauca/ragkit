using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;

namespace RagKit.Dashboard.Tests;

public class DashboardTests
{
    [Fact]
    public async Task MapRagDashboard_serves_the_embedded_index_page()
    {
        var (app, client) = await TestSupport.BuildHostAsync(await TestSupport.BuildRagAsync());
        await using var _ = app;

        var resp = await client.GetAsync("/rag-admin/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("RagKit", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MapRagDashboard_honors_a_custom_mount_path()
    {
        var (app, client) = await TestSupport.BuildHostAsync(await TestSupport.BuildRagAsync(), path: "/admin/rag");
        await using var _ = app;

        var resp = await client.GetAsync("/admin/rag/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Api_stats_resolves_RagClient_from_DI_and_reports_chunk_count()
    {
        var rag = await TestSupport.BuildRagAsync();
        await rag.DefineDomainAsync("docs");
        await rag.IngestAsync("contenido", "d.txt", domain: "docs");

        var (app, client) = await TestSupport.BuildHostAsync(rag);
        await using var _ = app;

        var resp = await client.GetAsync("/rag-admin/api/stats");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("\"chunkCount\":1", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Bare_mount_path_without_trailing_slash_redirects_to_the_index_page()
    {
        // Without this, relative fetch() calls in app.js would resolve against
        // the wrong base URL when a user hits the bare mount path (see
        // RagDashboardExtensions) — the browser needs the trailing slash before
        // any of the frontend's own relative requests fire.
        var (app, client) = await TestSupport.BuildHostAsync(await TestSupport.BuildRagAsync());
        await using var _ = app;

        var resp = await client.GetAsync("/rag-admin");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/rag-admin/", resp.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Serves_embedded_static_assets_other_than_the_index_page()
    {
        var (app, client) = await TestSupport.BuildHostAsync(await TestSupport.BuildRagAsync());
        await using var _ = app;

        var resp = await client.GetAsync("/rag-admin/app.js");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/javascript", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Unknown_asset_returns_404()
    {
        var (app, client) = await TestSupport.BuildHostAsync(await TestSupport.BuildRagAsync());
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
