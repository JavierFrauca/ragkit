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
    private readonly bool _supportsTools;

    public OpenAiChatClient(string baseUrl, string apiKey, string model, HttpClient? http = null, int timeoutSeconds = 300, bool supportsTools = true)
    {
        if (http is null) { _http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)) }; }
        else _http = http;
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _model = model;
        _supportsTools = supportsTools;
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

    public bool SupportsTools => _supportsTools;

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

    /// <summary>Streamed counterpart of <see cref="NextAsync"/>: same request shape
    /// (tools, tool_choice: auto) but <c>stream: true</c>. Unlike plain content
    /// streaming, tool calls arrive fragmented by index — a call's <c>id</c>/
    /// <c>function.name</c> only in the first delta for that index, <c>function.
    /// arguments</c> split across arbitrarily many subsequent deltas — so this
    /// accumulates by index and only yields <see cref="AgentDeltaKind.ToolCallsReady"/>
    /// once the stream ends. Not retried, like <see cref="StreamAsync"/>.</summary>
    public async IAsyncEnumerable<AgentDelta> NextStreamAsync(
        IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var msgArr = new JsonArray();
        foreach (var m in messages)
        {
            var o = new JsonObject { ["role"] = m.Role };
            o["content"] = m.Content;
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
            ["stream"] = true,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(root.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new RagKitException($"El LLM respondió {(int)resp.StatusCode}: {Truncate(err, 300)}");
        }

        var callIds = new Dictionary<int, string>();
        var callNames = new Dictionary<int, string>();
        var callArgs = new Dictionary<int, StringBuilder>();
        var startedIndexes = new HashSet<int>();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line["data:".Length..].Trim();
            if (data.Length == 0) continue;
            if (data == "[DONE]") break;

            // Only primitive values are pulled out of the JsonDocument here — a
            // JsonElement can't be yielded after `doc` (its `using`) is disposed.
            string? contentPiece = null;
            var newlyStarted = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(data);
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() == 0 || !choices[0].TryGetProperty("delta", out var delta))
                    continue;

                if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    contentPiece = c.GetString();

                if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in tcs.EnumerateArray())
                    {
                        int index = tc.TryGetProperty("index", out var ix) ? ix.GetInt32() : 0;
                        if (tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                            callIds[index] = idEl.GetString() ?? "";
                        if (!tc.TryGetProperty("function", out var fn)) continue;
                        if (fn.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                        {
                            var name = nameEl.GetString() ?? "";
                            callNames[index] = name;
                            if (name.Length > 0 && startedIndexes.Add(index))
                                newlyStarted.Add(name);
                        }
                        if (fn.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String)
                        {
                            if (!callArgs.TryGetValue(index, out var sb)) callArgs[index] = sb = new StringBuilder();
                            sb.Append(argsEl.GetString());
                        }
                    }
                }
            }
            catch (JsonException) { continue; }

            if (!string.IsNullOrEmpty(contentPiece))
                yield return new AgentDelta(AgentDeltaKind.ContentPiece, Content: contentPiece);
            foreach (var name in newlyStarted)
                yield return new AgentDelta(AgentDeltaKind.ToolCallStarted, ToolName: name);
        }

        if (callIds.Count > 0 || callNames.Count > 0 || callArgs.Count > 0)
        {
            var indexes = callIds.Keys.Union(callNames.Keys).Union(callArgs.Keys).OrderBy(i => i);
            var final = indexes.Select(i => new ToolCall(
                callIds.TryGetValue(i, out var id) ? id : "",
                callNames.TryGetValue(i, out var name) ? name : "",
                callArgs.TryGetValue(i, out var sb) ? sb.ToString() : "{}")).ToList();
            yield return new AgentDelta(AgentDeltaKind.ToolCallsReady, ToolCalls: final);
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

/// <summary>Friendly error surfaced to the consumer (no stack-trace spelunking).</summary>
public sealed class RagKitException : Exception
{
    public RagKitException(string message) : base(message) { }
}
