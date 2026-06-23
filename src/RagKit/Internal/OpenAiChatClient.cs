using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RagKit.Internal;

/// <summary>
/// Minimal client for the OpenAI-compatible `/chat/completions` endpoint — works
/// with OpenAI, DeepSeek, Groq, Mistral and local servers (Ollama/vLLM/LM Studio)
/// by just changing the base URL and key. No SDK dependency, only HttpClient.
/// </summary>
internal sealed class OpenAiChatClient : IChatClient
{
    private readonly HttpClient _http;
    private readonly string _model;

    public OpenAiChatClient(string baseUrl, string apiKey, string model, HttpClient? http = null, int timeoutSeconds = 300)
    {
        if (http is null) { _http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)) }; }
        else _http = http;
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _model = model;
    }

    public async Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
    {
        var payload = new
        {
            model = _model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
        };
        var json = JsonSerializer.Serialize(payload);
        using var resp = await HttpRetry.PostAsync(_http, "chat/completions",
            () => new StringContent(json, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new RagKitException($"El LLM respondió {(int)resp.StatusCode}: {Truncate(body, 300)}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }

    /// <summary>Stream the answer by reading the server's <c>text/event-stream</c>
    /// (<c>stream:true</c>), yielding each <c>choices[].delta.content</c> piece.
    /// Not retried (the response is consumed incrementally); the caller can retry
    /// the whole call.</summary>
    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var payload = new
        {
            model = _model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            stream = true,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new RagKitException($"El LLM respondió {(int)resp.StatusCode}: {Truncate(err, 300)}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line["data:".Length..].Trim();
            if (data.Length == 0) continue;
            if (data == "[DONE]") yield break;

            string? piece = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() > 0 &&
                    choices[0].TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("content", out var c) &&
                    c.ValueKind == JsonValueKind.String)
                    piece = c.GetString();
            }
            catch (JsonException) { continue; } // tolerate keep-alive / non-JSON lines
            if (!string.IsNullOrEmpty(piece)) yield return piece;
        }
    }

    // RagKit assumes an OpenAI-compatible endpoint, which speaks tool-calling. If
    // the chosen model doesn't, the agent loop catches the failure / the caller
    // can use one-shot instead.
    public bool SupportsTools => true;

    public async Task<AgentTurn> NextAsync(IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools, CancellationToken ct = default)
    {
        // NOTE: the request/response JSON here follows the OpenAI tool-calling
        // spec; it is exercised by the agent-loop tests via a fake client, but the
        // wire format against a live endpoint should be validated end-to-end.
        var msgArr = new JsonArray();
        foreach (var m in messages)
        {
            var o = new JsonObject { ["role"] = m.Role };
            o["content"] = m.Content; // may be null (assistant tool-call turns)
            if (m.ToolCallId is not null) o["tool_call_id"] = m.ToolCallId;
            if (m.ToolCalls is { Count: > 0 })
            {
                var calls = new JsonArray();
                foreach (var c in m.ToolCalls)
                    calls.Add(new JsonObject
                    {
                        ["id"] = c.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.ArgumentsJson },
                    });
                o["tool_calls"] = calls;
            }
            msgArr.Add(o);
        }

        var toolArr = new JsonArray();
        foreach (var t in tools)
            toolArr.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = JsonNode.Parse(t.ParametersSchema),
                },
            });

        var root = new JsonObject
        {
            ["model"] = _model,
            ["messages"] = msgArr,
            ["tools"] = toolArr,
            ["tool_choice"] = "auto",
        };

        var json = root.ToJsonString();
        using var resp = await HttpRetry.PostAsync(_http, "chat/completions",
            () => new StringContent(json, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new RagKitException($"El LLM respondió {(int)resp.StatusCode}: {Truncate(body, 300)}");

        using var doc = JsonDocument.Parse(body);
        var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        var text = message.TryGetProperty("content", out var c0) && c0.ValueKind == JsonValueKind.String ? c0.GetString() : null;

        var toolCalls = new List<ToolCall>();
        if (message.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
        {
            foreach (var tc in tcs.EnumerateArray())
            {
                var fn = tc.GetProperty("function");
                toolCalls.Add(new ToolCall(
                    tc.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    fn.GetProperty("name").GetString() ?? "",
                    fn.TryGetProperty("arguments", out var ar) ? ar.GetString() ?? "{}" : "{}"));
            }
        }
        return new AgentTurn(text, toolCalls);
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

/// <summary>Friendly error surfaced to the consumer (no stack-trace spelunking).</summary>
public sealed class RagKitException : Exception
{
    public RagKitException(string message) : base(message) { }
}
