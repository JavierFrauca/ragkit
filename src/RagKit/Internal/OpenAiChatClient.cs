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
    // Models known to write tool calls as XML in the content stream rather than via the
    // OpenAI JSON function-calling protocol. ParseXmlToolCalls=null → auto-enable for these.
    private static readonly HashSet<string> _xmlToolCallModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "deepseek-v4-pro",
        "deepseek-r1",
        "deepseek-reasoner",
    };

    private readonly HttpClient _http;
    private readonly string _model;
    private readonly bool _supportsTools;
    private readonly bool _parseXmlToolCalls;

    public OpenAiChatClient(string baseUrl, string apiKey, string model, HttpClient? http = null,
        int timeoutSeconds = 300, bool supportsTools = true, bool? parseXmlToolCalls = null)
    {
        if (http is null) { _http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)) }; }
        else _http = http;
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _model = model;
        _supportsTools = supportsTools;
        _parseXmlToolCalls = parseXmlToolCalls ?? _xmlToolCallModels.Contains(model);
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
        // When ParseXmlToolCalls: accumulate content instead of streaming it, then
        // parse XML tool calls from the accumulated text at stream end.
        var accumContent = _parseXmlToolCalls ? new StringBuilder() : null;

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
            {
                if (accumContent is not null)
                    accumContent.Append(contentPiece); // ParseXmlToolCalls: buffer for post-stream parsing
                else
                    yield return new AgentDelta(AgentDeltaKind.ContentPiece, Content: contentPiece);
            }
            foreach (var name in newlyStarted)
                yield return new AgentDelta(AgentDeltaKind.ToolCallStarted, ToolName: name);
        }

        // ── ParseXmlToolCalls: detect and emit XML-format tool calls ──────────────
        if (accumContent is not null)
        {
            var (cleanText, xmlCalls) = ExtractXmlToolCalls(accumContent.ToString());
            if (!string.IsNullOrWhiteSpace(cleanText))
                yield return new AgentDelta(AgentDeltaKind.ContentPiece, Content: cleanText);

            // Prefer JSON tool calls from delta.tool_calls; fall back to XML-parsed ones.
            bool hasJsonCalls = callIds.Count > 0 || callNames.Count > 0 || callArgs.Count > 0;
            if (!hasJsonCalls && xmlCalls.Count > 0)
            {
                foreach (var (n, _) in xmlCalls)
                    yield return new AgentDelta(AgentDeltaKind.ToolCallStarted, ToolName: n);
                var xmlFinal = xmlCalls
                    .Select((tc, i) => new ToolCall($"xml-{i}", tc.Name, tc.ArgsJson))
                    .ToList();
                yield return new AgentDelta(AgentDeltaKind.ToolCallsReady, ToolCalls: xmlFinal);
                yield break; // XML tool calls handled; skip the JSON emission below
            }
            // No XML tool calls found (final answer turn) — fall through to JSON check.
        }

        // ── Standard JSON tool calls ──────────────────────────────────────────────
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

    /// <summary>
    /// Parses content written by a model that uses XML for tool calls (e.g. deepseek-v4-pro).
    /// Strips <c>&lt;thinking&gt;</c> / <c>&lt;think&gt;</c> and <c>&lt;tool_calls&gt;</c>
    /// wrappers; extracts individual <c>&lt;tag_with_underscore&gt;</c> blocks as tool calls;
    /// returns everything else as clean visible content.
    /// </summary>
    internal static (string CleanContent, List<(string Name, string ArgsJson)> ToolCalls)
        ExtractXmlToolCalls(string raw)
    {
        var result = new List<(string, string)>();
        var clean = new StringBuilder();
        int pos = 0;

        while (pos < raw.Length)
        {
            int lt = raw.IndexOf('<', pos);
            if (lt < 0) { clean.Append(raw[pos..]); break; }
            clean.Append(raw[pos..lt]); // text before '<'

            if (TrySkipBlock(raw, lt, "<thinking>", "</thinking>", out int after) ||
                TrySkipBlock(raw, lt, "<think>", "</think>", out after))
            { pos = after; continue; }

            if (TrySkipBlock(raw, lt, "<tool_calls>", "</tool_calls>", out after))
            {
                var inner = raw[(lt + "<tool_calls>".Length)..
                    Math.Max(lt + "<tool_calls>".Length, after - "</tool_calls>".Length)];
                ExtractInnerTools(inner, result);
                pos = after; continue;
            }

            if (TryReadUnderscoreTag(raw, lt, out var tagName, out var tagContent, out after))
            {
                result.Add((tagName, CoerceToArgsJson(tagContent)));
                pos = after; continue;
            }

            clean.Append('<'); // regular '<', emit literally
            pos = lt + 1;
        }

        return (clean.ToString().Trim(), result);
    }

    private static bool TrySkipBlock(string s, int start, string open, string close, out int after)
    {
        if (!s.AsSpan(start).StartsWith(open.AsSpan(), StringComparison.Ordinal)) { after = 0; return false; }
        int end = s.IndexOf(close, start + open.Length, StringComparison.Ordinal);
        after = end >= 0 ? end + close.Length : s.Length;
        return true;
    }

    private static bool TryReadUnderscoreTag(string s, int lt,
        out string tagName, out string content, out int after)
    {
        tagName = ""; content = ""; after = 0;
        int k = lt + 1;
        if (k >= s.Length || !char.IsAsciiLetterLower(s[k])) return false;
        bool hasUnderscore = false;
        while (k < s.Length && (char.IsAsciiLetterLower(s[k]) || char.IsAsciiDigit(s[k]) || s[k] == '_'))
        { if (s[k] == '_') hasUnderscore = true; k++; }
        if (!hasUnderscore || k >= s.Length || s[k] != '>') return false;
        var name = s[(lt + 1)..k];
        var closeTag = "</" + name + ">";
        int closeAt = s.IndexOf(closeTag, k + 1, StringComparison.Ordinal);
        if (closeAt < 0) return false;
        tagName = name; content = s[(k + 1)..closeAt].Trim(); after = closeAt + closeTag.Length;
        return true;
    }

    private static void ExtractInnerTools(string inner, List<(string, string)> result)
    {
        int pos = 0;
        while (pos < inner.Length)
        {
            int lt = inner.IndexOf('<', pos);
            if (lt < 0) break;
            if (TryReadUnderscoreTag(inner, lt, out var n, out var c, out int after))
            { result.Add((n, CoerceToArgsJson(c))); pos = after; }
            else pos = lt + 1;
        }
    }

    private static string CoerceToArgsJson(string content)
    {
        var trimmed = content.Trim();
        if ((trimmed.StartsWith('{') && trimmed.EndsWith('}')) ||
            (trimmed.StartsWith('[') && trimmed.EndsWith(']')))
            return trimmed; // already JSON
        // Plain text: wrap as "query" (covers search_knowledge_base, the primary tool)
        return JsonSerializer.Serialize(new { query = trimmed });
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

/// <summary>Friendly error surfaced to the consumer (no stack-trace spelunking).</summary>
public sealed class RagKitException : Exception
{
    public RagKitException(string message) : base(message) { }
}
