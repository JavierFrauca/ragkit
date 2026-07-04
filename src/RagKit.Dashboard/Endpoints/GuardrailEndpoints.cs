using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace RagKit.Dashboard.Endpoints;

internal sealed record GuardrailRequest(string Description, string Stage = "Input", string? Domain = null, string? Profile = null)
{
    public GuardrailRule ToRule() => new(Description, Enum.Parse<GuardrailStage>(Stage, ignoreCase: true), Domain, Profile);
}

internal static class GuardrailEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/api/guardrails", async (RagClient rag, CancellationToken ct) =>
            Results.Json(await rag.ListGuardrailsAsync(ct).ConfigureAwait(false), DashboardJson.Options));

        group.MapPost("/api/guardrails", async (RagClient rag, GuardrailRequest req, CancellationToken ct) =>
        {
            var rule = await rag.DefineGuardrailAsync(req.ToRule(), ct).ConfigureAwait(false);
            return Results.Json(rule, DashboardJson.Options);
        });

        // Guardrails have no id — removal is by structural value equality (RagClient.RemoveGuardrailAsync),
        // so the exact rule (same Description/Stage/Domain/Profile) travels in the DELETE body.
        group.MapDelete("/api/guardrails", async (RagClient rag, [FromBody] GuardrailRequest req, CancellationToken ct) =>
        {
            var removed = await rag.RemoveGuardrailAsync(req.ToRule(), ct).ConfigureAwait(false);
            return removed ? Results.Ok() : Results.NotFound();
        });
    }
}
