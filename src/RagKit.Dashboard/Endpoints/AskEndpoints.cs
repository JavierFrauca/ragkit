using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace RagKit.Dashboard.Endpoints;

internal sealed record AskRequest(string Question, string? Domain, string? Profile);

internal static class AskEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/api/ask", async (AskRequest req, RagClient rag, CancellationToken ct) =>
        {
            var answer = await rag.AskAsync(req.Question, req.Domain, null, req.Profile, ct).ConfigureAwait(false);
            return Results.Json(answer, DashboardJson.Options);
        });

        // GET (not POST) so the browser's EventSource — which only issues GET, no
        // body — can consume it directly from the playground UI.
        group.MapGet("/api/ask/stream", async (string question, string? domain, string? profile, RagClient rag, CancellationToken ct) =>
        {
            var stream = await rag.AskStreamAsync(question, domain, null, profile, ct).ConfigureAwait(false);
            return TypedResults.ServerSentEvents(StreamAskEventsAsync(stream, ct), eventType: "ask");
        });
    }

    // Citations first (RagStream.Citations is already resolved before any token
    // exists), then one event per token, then a final marker — raw-string SSE
    // (not TypedResults.ServerSentEvents<T>) so every event serializes with
    // DashboardJson.Options regardless of the host app's own JSON configuration.
    private static async IAsyncEnumerable<string> StreamAskEventsAsync(RagStream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return JsonSerializer.Serialize(new { citations = stream.Citations }, DashboardJson.Options);

        await foreach (var token in stream.Tokens.WithCancellation(ct))
            yield return JsonSerializer.Serialize(new { token }, DashboardJson.Options);

        yield return JsonSerializer.Serialize(new { done = true }, DashboardJson.Options);
    }
}
