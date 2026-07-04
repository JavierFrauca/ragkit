using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace RagKit.Dashboard.Endpoints;

internal sealed record IngestRequest(string Path, string? Domain, bool Recursive = true);

internal static class IngestEndpoints
{
    // One tracker per mounted dashboard: a run started by one request must still be
    // readable by a later GET .../stream request, so this can't be per-request state.
    private static readonly IngestRunTracker Tracker = new();

    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/api/ingest", (IngestRequest req, RagClient rag) =>
        {
            if (!Directory.Exists(req.Path))
                return Results.BadRequest(new { error = $"La carpeta '{req.Path}' no existe en el servidor." });

            var runId = Tracker.Start(rag, req.Path, req.Domain, req.Recursive);
            return Results.Json(new { runId }, DashboardJson.Options);
        });

        group.MapGet("/api/ingest/{runId}/stream", (Guid runId, CancellationToken ct) =>
        {
            if (!Tracker.TryGet(runId, out var run)) return Results.NotFound();
            return TypedResults.ServerSentEvents(StreamEventsAsync(run, ct), eventType: "ingest");
        });
    }

    // Raw-string SSE (not TypedResults.ServerSentEvents<T>) so every event is
    // serialized with DashboardJson.Options — the host app's own JSON options
    // (unknown to this library) never come into play.
    private static async IAsyncEnumerable<string> StreamEventsAsync(IngestRun run, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var result in run.Channel.Reader.ReadAllAsync(ct))
            yield return JsonSerializer.Serialize(new { result }, DashboardJson.Options);

        yield return JsonSerializer.Serialize(
            new { done = true, status = run.Status.ToString(), error = run.Error }, DashboardJson.Options);
    }
}
