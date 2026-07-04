using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace RagKit.Dashboard.Endpoints;

internal static class StatsEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/api/stats", async (RagClient rag, CancellationToken ct) =>
        {
            var chunkCount = await rag.ChunkCountAsync(ct).ConfigureAwait(false);
            var domains = await rag.ListDomainsAsync(ct).ConfigureAwait(false);
            var documents = await rag.ListDocumentsAsync(ct: ct).ConfigureAwait(false);
            return Results.Json(new
            {
                chunkCount,
                domainCount = domains.Count,
                documentCount = documents.Count,
            }, DashboardJson.Options);
        });
    }
}
