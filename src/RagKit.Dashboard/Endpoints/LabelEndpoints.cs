using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace RagKit.Dashboard.Endpoints;

internal sealed record CreateLabelRequest(string Name, string Description = "");

internal static class LabelEndpoints
{
    // No delete: RagClient itself has no RemoveLabelAsync — labels are create/list only.
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/api/labels", async (RagClient rag, CancellationToken ct) =>
            Results.Json(await rag.ListLabelsAsync(ct).ConfigureAwait(false), DashboardJson.Options));

        group.MapPost("/api/labels", async (RagClient rag, CreateLabelRequest req, CancellationToken ct) =>
        {
            var label = await rag.DefineLabelAsync(req.Name, req.Description, ct).ConfigureAwait(false);
            return Results.Json(label, DashboardJson.Options);
        });
    }
}
