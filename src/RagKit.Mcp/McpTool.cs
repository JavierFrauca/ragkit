using RagKit;

namespace RagKit.Mcp;

/// <summary>Adapts one MCP server tool to RagKit's <see cref="IRagTool"/> so the
/// agent loop can call it like any internal tool.</summary>
public sealed class McpTool : IRagTool
{
    private readonly IMcpConnection _conn;
    private readonly McpToolInfo _info;

    public McpTool(IMcpConnection conn, McpToolInfo info)
    {
        _conn = conn;
        _info = info;
    }

    public string Name => _info.Name;
    public string Description => _info.Description;
    public string ParametersSchema =>
        string.IsNullOrWhiteSpace(_info.InputSchema) ? """{"type":"object","properties":{}}""" : _info.InputSchema;

    public Task<string> InvokeAsync(string argumentsJson, CancellationToken ct = default)
        => _conn.CallToolAsync(_info.Name, argumentsJson, ct);
}

/// <summary>Helpers to connect to MCP servers and turn their tools into RagKit tools.</summary>
public static class McpTools
{
    /// <summary>
    /// Launch an MCP server over **stdio** (a child process speaking JSON-RPC on
    /// stdin/stdout) and complete the initialize handshake. Example:
    /// <c>await McpTools.ConnectStdioAsync("npx", "-y", "@modelcontextprotocol/server-everything", "stdio")</c>.
    /// </summary>
    public static Task<IMcpConnection> ConnectStdioAsync(string command, params string[] args)
        => StdioMcpClient.StartAsync(command, args);

    /// <summary>Wrap every tool the server advertises as an <see cref="IRagTool"/>.</summary>
    public static async Task<IReadOnlyList<IRagTool>> FromConnectionAsync(IMcpConnection conn, CancellationToken ct = default)
    {
        var tools = await conn.ListToolsAsync(ct).ConfigureAwait(false);
        return tools.Select(t => (IRagTool)new McpTool(conn, t)).ToList();
    }

    /// <summary>Connect to a stdio MCP server and register all its tools on the client.</summary>
    public static async Task<IMcpConnection> AddStdioServerAsync(this RagClient rag, string command, params string[] args)
    {
        var conn = await ConnectStdioAsync(command, args).ConfigureAwait(false);
        foreach (var tool in await FromConnectionAsync(conn).ConfigureAwait(false))
            rag.RegisterTool(tool);
        return conn;
    }
}
