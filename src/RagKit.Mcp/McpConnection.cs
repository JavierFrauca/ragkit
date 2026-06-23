using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RagKit.Mcp;

/// <summary>A tool advertised by an MCP server.</summary>
public sealed record McpToolInfo(string Name, string Description, string InputSchema);

/// <summary>A connection to an MCP server.</summary>
public interface IMcpConnection : IDisposable
{
    Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct = default);
    Task<string> CallToolAsync(string name, string argumentsJson, CancellationToken ct = default);
}

/// <summary>
/// Minimal MCP client over stdio JSON-RPC 2.0 — no SDK dependency. A background
/// reader correlates responses by id; notifications/logs are ignored.
/// </summary>
internal sealed class StdioMcpClient : IMcpConnection
{
    private readonly Process _proc;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private int _id;

    private StdioMcpClient(Process proc)
    {
        _proc = proc;
        _ = Task.Run(ReadLoop);
    }

    public static async Task<IMcpConnection> StartAsync(string command, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var proc = Process.Start(psi) ?? throw new InvalidOperationException($"No se pudo iniciar '{command}'.");
        var client = new StdioMcpClient(proc);

        await client.RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "ragkit", ["version"] = "0.1" },
        }).ConfigureAwait(false);
        client.Notify("notifications/initialized", new JsonObject());
        return client;
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct = default)
    {
        var result = await RequestAsync("tools/list", new JsonObject()).ConfigureAwait(false);
        var list = new List<McpToolInfo>();
        if (result.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tools.EnumerateArray())
            {
                var schema = t.TryGetProperty("inputSchema", out var s) ? s.GetRawText() : "{}";
                list.Add(new McpToolInfo(
                    t.GetProperty("name").GetString() ?? "",
                    t.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                    schema));
            }
        }
        return list;
    }

    public async Task<string> CallToolAsync(string name, string argumentsJson, CancellationToken ct = default)
    {
        var args = string.IsNullOrWhiteSpace(argumentsJson) ? new JsonObject() : JsonNode.Parse(argumentsJson);
        var result = await RequestAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = args }).ConfigureAwait(false);
        // Concatenate the text content blocks.
        var sb = new StringBuilder();
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
                if (block.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                    sb.AppendLine(txt.GetString());
        return sb.ToString().Trim();
    }

    private async Task<JsonElement> RequestAsync(string method, JsonNode @params)
    {
        int id = Interlocked.Increment(ref _id);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params };
        WriteLine(msg.ToJsonString());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using (timeout.Token.Register(() => tcs.TrySetException(new TimeoutException($"MCP '{method}' agotó el tiempo."))))
        {
            var resp = await tcs.Task.ConfigureAwait(false);
            if (resp.TryGetProperty("error", out var err))
                throw new InvalidOperationException($"MCP error en '{method}': {err.GetRawText()}");
            return resp.TryGetProperty("result", out var result) ? result : default;
        }
    }

    private void Notify(string method, JsonNode @params)
        => WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = @params }.ToJsonString());

    private void WriteLine(string json)
    {
        lock (_proc) { _proc.StandardInput.WriteLine(json); _proc.StandardInput.Flush(); }
    }

    private async Task ReadLoop()
    {
        try
        {
            string? line;
            while ((line = await _proc.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                line = line.Trim();
                if (line.Length == 0) continue;
                JsonElement root;
                try { root = JsonDocument.Parse(line).RootElement.Clone(); }
                catch { continue; } // not JSON (a log line) — ignore
                if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id)
                    && _pending.TryRemove(id, out var tcs))
                    tcs.TrySetResult(root);
            }
        }
        catch { /* process ended */ }
    }

    public void Dispose()
    {
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
        _proc.Dispose();
    }
}
