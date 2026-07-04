using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace RagKit.Dashboard.Endpoints;

internal static class DocumentEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/api/documents", async (RagClient rag, string? domain, CancellationToken ct) =>
            Results.Json(await rag.ListDocumentsAsync(domain, ct).ConfigureAwait(false), DashboardJson.Options));

        group.MapDelete("/api/documents/{source}", async (string source, string? domain, RagClient rag, CancellationToken ct) =>
        {
            var removed = await rag.RemoveDocumentAsync(source, domain, ct).ConfigureAwait(false);
            return Results.Json(new { removedChunks = removed }, DashboardJson.Options);
        });

        // Cursor is opaque (backend-specific — see ChunkPage) — the UI just round-trips
        // whatever NextCursor came back, it never inspects or builds one itself.
        group.MapGet("/api/documents/{source}/chunks", async (string source, string? domain, int? take, string? cursor, RagClient rag, CancellationToken ct) =>
        {
            var page = await rag.ListChunksAsync(source, domain, take ?? 50, cursor, ct).ConfigureAwait(false);
            return Results.Json(page, DashboardJson.Options);
        });
    }
}
