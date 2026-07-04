using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RagKit;

namespace RagKit.Dashboard.Tests;

/// <summary>A chat client that returns a fixed response — enough to build a
/// working RagClient for these host-level tests (no LLM behavior is exercised).</summary>
sealed class FakeChat(string response) : IChatClient
{
    public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
        => Task.FromResult(response);
}

static class TestSupport
{
    public static async Task<RagClient> BuildRagAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ragkit-dashboard-test-" + Guid.NewGuid().ToString("N"));
        var store = new InMemoryVectorStore(dir);
        var embedder = new LocalEmbedder();
        var rag = new RagClient(new RagOptions(), embedder, store, new FakeChat("ok"), new FakeChat("{}"));
        await store.InitializeAsync(embedder.ModelId, embedder.Dimension);
        return rag;
    }

    public static async Task<(WebApplication App, HttpClient Client)> BuildHostAsync(RagClient rag, string? path = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(rag);
        var app = builder.Build();
        if (path is null) app.MapRagDashboard();
        else app.MapRagDashboard(path);
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    /// <summary>Parses a fully-buffered SSE response body (the stream must have
    /// already ended — no use for a genuinely long-lived connection) into the JSON
    /// payload of each "data:" line, in order.</summary>
    public static IReadOnlyList<JsonElement> ParseSseEvents(string body) =>
        body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(block => block.Split('\n').FirstOrDefault(l => l.StartsWith("data: ")))
            .Where(dataLine => dataLine is not null)
            .Select(dataLine => JsonSerializer.Deserialize<JsonElement>(dataLine!["data: ".Length..]))
            .ToList();
}
