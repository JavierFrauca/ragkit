using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace RagKit.Dashboard.Endpoints;

/// <summary>Always sets both fields (a null value resets that prompt to its default) —
/// the UI fetches the current state first and round-trips whichever field it didn't edit.</summary>
internal sealed record SetPromptsRequest(string? OneShotPrompt, string? ChatPrompt);

internal sealed record SetDomainPromptRequest(string Prompt);

internal static class PromptEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/api/prompts", (RagClient rag) => Results.Json(new
        {
            oneShotPrompt = rag.OneShotPrompt,
            chatPrompt = rag.ChatPrompt,
            domainPrompts = rag.DomainPrompts,
        }, DashboardJson.Options));

        group.MapPut("/api/prompts", (RagClient rag, SetPromptsRequest req) =>
        {
            rag.OneShotPrompt = req.OneShotPrompt;
            rag.ChatPrompt = req.ChatPrompt;
            return Results.Ok();
        });

        group.MapPut("/api/prompts/domain/{domain}", (string domain, RagClient rag, SetDomainPromptRequest req) =>
        {
            rag.SetDomainPrompt(domain, req.Prompt);
            return Results.Ok();
        });

        group.MapDelete("/api/prompts/domain/{domain}", (string domain, RagClient rag) =>
            rag.RemoveDomainPrompt(domain) ? Results.Ok() : Results.NotFound());
    }
}
