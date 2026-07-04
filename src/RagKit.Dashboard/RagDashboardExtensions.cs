using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RagKit.Dashboard.Endpoints;

namespace RagKit.Dashboard;

/// <summary>
/// Mounts the RagKit maintenance dashboard (static assets + a small JSON API over
/// <see cref="RagClient"/>) at a path of your choosing. See the package README for
/// the security caveat: there is no built-in authentication.
/// </summary>
public static class RagDashboardExtensions
{
    private static readonly Assembly Assembly = typeof(RagDashboardExtensions).Assembly;
    private const string ResourcePrefix = "RagKit.Dashboard.wwwroot.";

    /// <summary>
    /// Map the dashboard under <paramref name="path"/> (default <c>/rag-admin</c>).
    /// Requires a <see cref="RagClient"/> registered in the DI container (e.g.
    /// <c>services.AddSingleton(myRagClient)</c>) — the dashboard resolves it per
    /// request rather than taking it as a parameter here, so its lifetime is fully
    /// owned by your app's container.
    /// </summary>
    /// <returns>
    /// An <see cref="IEndpointConventionBuilder"/> so you can chain your own auth,
    /// e.g. <c>app.MapRagDashboard().RequireAuthorization("AdminOnly")</c> — the
    /// dashboard doesn't implement any authentication of its own.
    /// </returns>
    public static IEndpointConventionBuilder MapRagDashboard(this IEndpointRouteBuilder endpoints, string path = "/rag-admin")
    {
        var normalized = "/" + path.Trim('/');
        var group = endpoints.MapGroup(normalized);

        // JSON API — one file per resource under Endpoints/. Mapped before the static
        // asset catch-all for readability; ASP.NET Core's routing matches by
        // specificity regardless of registration order, so there's no actual conflict
        // between e.g. "/api/domains" and the "/{**file}" catch-all below.
        StatsEndpoints.Map(group);
        DomainEndpoints.Map(group);
        LabelEndpoints.Map(group);
        DocumentEndpoints.Map(group);
        GuardrailEndpoints.Map(group);
        ProfileEndpoints.Map(group);
        PromptEndpoints.Map(group);
        IngestEndpoints.Map(group);

        // Static assets (index.html, css, js…), embedded in the assembly — no
        // frontend build step for the consumer. A single catch-all handles both
        // the bare path (file: "") and any sub-path. The frontend's own fetch()
        // calls use paths relative to the current page (e.g. "api/domains"), which
        // only resolve correctly if the browser's address bar ends in "/" — so a
        // request for the bare mount path (no trailing slash) redirects once
        // instead of silently serving index.html at the wrong base URL.
        group.MapGet("/{**file}", (HttpContext context, string? file) =>
        {
            if (!string.IsNullOrEmpty(file)) return ServeEmbedded(file);
            var rawPath = context.Request.Path.Value ?? "";
            return rawPath.EndsWith('/') ? ServeEmbedded("index.html") : Results.Redirect(rawPath + "/");
        });

        return group;
    }

    private static IResult ServeEmbedded(string file)
    {
        var resourceName = ResourcePrefix + file.Replace('/', '.');
        var stream = Assembly.GetManifestResourceStream(resourceName);
        return stream is null ? Results.NotFound() : Results.Stream(stream, ContentTypeOf(file));
    }

    private static string ContentTypeOf(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css",
        ".js" => "text/javascript",
        ".json" => "application/json",
        _ => "application/octet-stream",
    };
}
