using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace RagKit.Dashboard.Tests;

public class CrudEndpointsTests
{
    [Fact]
    public async Task Domains_can_be_created_listed_and_deleted()
    {
        var rag = await TestSupport.BuildRagAsync();
        var (app, client) = await TestSupport.BuildHostAsync(rag);
        await using var _ = app;

        var create = await client.PostAsJsonAsync("/rag-admin/api/domains", new { name = "fiscal", description = "impuestos" });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        var list = await client.GetFromJsonAsync<JsonElement>("/rag-admin/api/domains");
        Assert.Contains(list.EnumerateArray(), d => d.GetProperty("name").GetString() == "fiscal");

        var del = await client.DeleteAsync("/rag-admin/api/domains/fiscal");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var listAfter = await client.GetFromJsonAsync<JsonElement>("/rag-admin/api/domains");
        Assert.DoesNotContain(listAfter.EnumerateArray(), d => d.GetProperty("name").GetString() == "fiscal");
    }

    [Fact]
    public async Task Labels_can_be_created_and_listed()
    {
        var rag = await TestSupport.BuildRagAsync();
        var (app, client) = await TestSupport.BuildHostAsync(rag);
        await using var _ = app;

        await client.PostAsJsonAsync("/rag-admin/api/labels", new { name = "iva" });
        var list = await client.GetFromJsonAsync<JsonElement>("/rag-admin/api/labels");
        Assert.Contains(list.EnumerateArray(), l => l.GetProperty("name").GetString() == "iva");
    }

    [Fact]
    public async Task Documents_can_be_listed_and_deleted()
    {
        var rag = await TestSupport.BuildRagAsync();
        await rag.DefineDomainAsync("docs");
        await rag.IngestAsync("contenido", "d.txt", domain: "docs");

        var (app, client) = await TestSupport.BuildHostAsync(rag);
        await using var _ = app;

        var list = await client.GetFromJsonAsync<JsonElement>("/rag-admin/api/documents?domain=docs");
        Assert.Contains(list.EnumerateArray(), d => d.GetProperty("source").GetString() == "d.txt");

        var del = await client.DeleteAsync("/rag-admin/api/documents/d.txt?domain=docs");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var listAfter = await client.GetFromJsonAsync<JsonElement>("/rag-admin/api/documents?domain=docs");
        Assert.Empty(listAfter.EnumerateArray());
    }

    [Fact]
    public async Task Chunks_can_be_paginated_via_cursor()
    {
        var rag = await TestSupport.BuildRagAsync();
        await rag.DefineDomainAsync("docs");
        await rag.IngestAsync("uno", "multi.txt", domain: "docs");
        await rag.IngestAsync("dos", "multi.txt", domain: "docs"); // second ingest just adds another chunk under the same source in this in-memory test store

        var (app, client) = await TestSupport.BuildHostAsync(rag);
        await using var _ = app;

        var page1 = await client.GetFromJsonAsync<JsonElement>("/rag-admin/api/documents/multi.txt/chunks?domain=docs&take=1");
        var items1 = page1.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items1);
        var cursor = page1.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);

        var page2 = await client.GetFromJsonAsync<JsonElement>($"/rag-admin/api/documents/multi.txt/chunks?domain=docs&take=1&cursor={cursor}");
        var items2 = page2.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items2);
        Assert.NotEqual(items1[0].GetProperty("id").GetString(), items2[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Guardrails_can_be_created_listed_and_deleted()
    {
        var rag = await TestSupport.BuildRagAsync();
        var (app, client) = await TestSupport.BuildHostAsync(rag);
        await using var _ = app;

        var body = new { description = "No reveles datos de terceros", stage = "Output", domain = (string?)null, profile = (string?)null };
        await client.PostAsJsonAsync("/rag-admin/api/guardrails", body);

        var list = await client.GetFromJsonAsync<JsonElement>("/rag-admin/api/guardrails");
        Assert.Contains(list.EnumerateArray(), r => r.GetProperty("description").GetString() == "No reveles datos de terceros");

        var req = new HttpRequestMessage(HttpMethod.Delete, "/rag-admin/api/guardrails") { Content = JsonContent.Create(body) };
        var del = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var listAfter = await client.GetFromJsonAsync<JsonElement>("/rag-admin/api/guardrails");
        Assert.Empty(listAfter.EnumerateArray());
    }

    [Fact]
    public async Task Profiles_can_be_created_listed_and_deleted()
    {
        var rag = await TestSupport.BuildRagAsync();
        await rag.DefineDomainAsync("construccion");
        var (app, client) = await TestSupport.BuildHostAsync(rag);
        await using var _ = app;

        await client.PostAsJsonAsync("/rag-admin/api/profiles", new { name = "electricista", domain = "construccion", prompt = "Eres electricista." });

        var list = await client.GetFromJsonAsync<JsonElement>("/rag-admin/api/profiles");
        Assert.Contains(list.EnumerateArray(), p => p.GetProperty("name").GetString() == "electricista");

        var del = await client.DeleteAsync("/rag-admin/api/profiles/construccion/electricista");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var listAfter = await client.GetFromJsonAsync<JsonElement>("/rag-admin/api/profiles");
        Assert.Empty(listAfter.EnumerateArray());
    }

    [Fact]
    public async Task Prompts_can_be_read_and_updated()
    {
        var rag = await TestSupport.BuildRagAsync();
        var (app, client) = await TestSupport.BuildHostAsync(rag);
        await using var _ = app;

        var before = await client.GetFromJsonAsync<JsonElement>("/rag-admin/api/prompts");
        Assert.Equal(JsonValueKind.Null, before.GetProperty("oneShotPrompt").ValueKind);

        await client.PutAsJsonAsync("/rag-admin/api/prompts", new { oneShotPrompt = "Eres formal.", chatPrompt = (string?)null });
        var after = await client.GetFromJsonAsync<JsonElement>("/rag-admin/api/prompts");
        Assert.Equal("Eres formal.", after.GetProperty("oneShotPrompt").GetString());

        await client.PutAsJsonAsync("/rag-admin/api/prompts/domain/fiscal", new { prompt = "Eres un asesor fiscal." });
        var withDomain = await client.GetFromJsonAsync<JsonElement>("/rag-admin/api/prompts");
        Assert.Equal("Eres un asesor fiscal.", withDomain.GetProperty("domainPrompts").GetProperty("fiscal").GetString());

        var del = await client.DeleteAsync("/rag-admin/api/prompts/domain/fiscal");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_domain_cascades_to_its_profiles_and_guardrails_via_http()
    {
        var rag = await TestSupport.BuildRagAsync();
        await rag.DefineDomainAsync("payroll");
        var (app, client) = await TestSupport.BuildHostAsync(rag);
        await using var _ = app;

        await client.PostAsJsonAsync("/rag-admin/api/profiles", new { name = "gestor", domain = "payroll" });
        await client.PostAsJsonAsync("/rag-admin/api/guardrails", new { description = "regla", stage = "Input", domain = "payroll" });

        await client.DeleteAsync("/rag-admin/api/domains/payroll");

        var profiles = await client.GetFromJsonAsync<JsonElement>("/rag-admin/api/profiles");
        Assert.Empty(profiles.EnumerateArray());
        var guardrails = await client.GetFromJsonAsync<JsonElement>("/rag-admin/api/guardrails");
        Assert.Empty(guardrails.EnumerateArray());
    }
}
