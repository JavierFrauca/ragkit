using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

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

        // Static assets (index.html, css, js…), embedded in the assembly — no
        // frontend build step for the consumer. A single catch-all handles both
        // the bare path (file: "") and any sub-path.
        group.MapGet("/{**file}", (string? file) => ServeEmbedded(string.IsNullOrEmpty(file) ? "index.html" : file));

        // First slice of the JSON API — proves the RagClient DI wiring end-to-end.
        // Domains/labels/documents/chunks/guardrails/profiles/prompts/ingest/ask
        // land in later milestones under the same group.
        group.MapGet("/api/stats", async (RagClient rag, CancellationToken ct) =>
        {
            var chunkCount = await rag.ChunkCountAsync(ct).ConfigureAwait(false);
            return Results.Ok(new { chunkCount });
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
