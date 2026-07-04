using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace RagKit.Dashboard.Endpoints;

internal sealed record ProfileRequest(string Name, string Domain, string Description = "", string? Prompt = null, IReadOnlyList<string>? Labels = null)
{
    public ProfileInfo ToProfile() => new(Name, Domain, Description, Prompt, Labels);
}

internal static class ProfileEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/api/profiles", async (RagClient rag, CancellationToken ct) =>
            Results.Json(await rag.ListProfilesAsync(ct).ConfigureAwait(false), DashboardJson.Options));

        group.MapPost("/api/profiles", async (RagClient rag, ProfileRequest req, CancellationToken ct) =>
        {
            var profile = await rag.DefineProfileAsync(req.ToProfile(), ct).ConfigureAwait(false);
            return Results.Json(profile, DashboardJson.Options);
        });

        group.MapDelete("/api/profiles/{domain}/{name}", async (string domain, string name, RagClient rag, CancellationToken ct) =>
        {
            var removed = await rag.RemoveProfileAsync(name, domain, ct).ConfigureAwait(false);
            return removed ? Results.Ok() : Results.NotFound();
        });
    }
}
