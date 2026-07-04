using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace RagKit.Dashboard.Endpoints;

internal sealed record CreateDomainRequest(string Name, string Description = "");

internal static class DomainEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/api/domains", async (RagClient rag, CancellationToken ct) =>
            Results.Json(await rag.ListDomainsAsync(ct).ConfigureAwait(false), DashboardJson.Options));

        group.MapPost("/api/domains", async (RagClient rag, CreateDomainRequest req, CancellationToken ct) =>
        {
            var domain = await rag.DefineDomainAsync(req.Name, req.Description, ct).ConfigureAwait(false);
            return Results.Json(domain, DashboardJson.Options);
        });

        // Removes the domain definition AND every chunk/profile/guardrail scoped to it
        // (RagClient.RemoveDomainAsync's cascade) — the UI must warn about this.
        group.MapDelete("/api/domains/{name}", async (string name, RagClient rag, CancellationToken ct) =>
        {
            var result = await rag.RemoveDomainAsync(name, ct).ConfigureAwait(false);
            if (!result.Existed) return Results.NotFound();
            return Results.Json(new { removedChunks = result.RemovedChunks }, DashboardJson.Options);
        });
    }
}
