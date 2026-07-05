using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RagKit.Dashboard.Tests;

/// <summary>A minimal auth scheme for testing <c>.RequireAuthorization()</c>: succeeds
/// only when the request carries an <c>X-Test-Auth</c> header — enough to prove the
/// dashboard actually enforces whatever auth a host wires up, not just that the
/// extensibility point compiles.</summary>
sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.ContainsKey("X-Test-Auth"))
            return Task.FromResult(AuthenticateResult.Fail("Missing X-Test-Auth header"));

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "test-user")], "Test");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), "Test");
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

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
    public async Task RequireAuthorization_actually_rejects_unauthenticated_requests()
    {
        // The dashboard implements no auth of its own — MapRagDashboard returns an
        // IEndpointConventionBuilder specifically so a host can chain its own scheme.
        // This wires up a real (if minimal) auth handler to prove .RequireAuthorization()
        // actually rejects/accepts requests, not just that chaining it compiles.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(await TestSupport.BuildRagAsync());
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", null);
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapRagDashboard().RequireAuthorization();
        await app.StartAsync();
        await using var _ = app;
        var client = app.GetTestClient();

        var unauthenticated = await client.GetAsync("/rag-admin/api/stats");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        client.DefaultRequestHeaders.Add("X-Test-Auth", "yes");
        var authenticated = await client.GetAsync("/rag-admin/api/stats");
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
    }
}
