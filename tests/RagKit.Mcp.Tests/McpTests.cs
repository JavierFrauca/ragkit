using RagKit.Mcp;

namespace RagKit.Mcp.Tests;

public class McpToolTests
{
    [Fact]
    public void McpTool_wraps_McpToolInfo()
    {
        var info = new McpToolInfo("echo", "repite texto",
            """{"type":"object","properties":{"text":{"type":"string"}}}""");
        var fakeConn = new FakeMcpConnection();
        var tool = new McpTool(fakeConn, info);

        Assert.Equal("echo", tool.Name);
        Assert.Equal("repite texto", tool.Description);
        Assert.Contains("\"text\"", tool.ParametersSchema);
    }

    [Fact]
    public async Task McpTool_InvokeAsync_delegates_to_connection()
    {
        var info = new McpToolInfo("calc", "suma", "{}");
        var fakeConn = new FakeMcpConnection();
        var tool = new McpTool(fakeConn, info);

        var result = await tool.InvokeAsync("""{"a":1,"b":2}""");

        Assert.Equal("calc", fakeConn.LastName);
        Assert.Equal("""{"a":1,"b":2}""", fakeConn.LastArgs);
    }
}

public class McpToolsTests
{
    [Fact]
    public async Task FromConnectionAsync_returns_all_tools()
    {
        var conn = new FakeMcpConnection(tools: new[]
        {
            new McpToolInfo("tool1", "primera", "{}"),
            new McpToolInfo("tool2", "segunda", """{"type":"object","properties":{}}"""),
        });

        var tools = await McpTools.FromConnectionAsync(conn);

        Assert.Equal(2, tools.Count);
        Assert.Equal("tool1", tools[0].Name);
        Assert.Equal("tool2", tools[1].Name);
    }

    [Fact]
    public async Task FromConnectionAsync_empty_server_returns_empty_list()
    {
        var conn = new FakeMcpConnection(tools: Array.Empty<McpToolInfo>());
        var tools = await McpTools.FromConnectionAsync(conn);
        Assert.Empty(tools);
    }
}

public class McpServersTests
{
    [Fact]
    public void Enable_registers_stdio_connector()
    {
        // Just verify it doesn't throw — connector registration is a prerequisite
        // for config-driven MCP via RagOptions.Mcps.
        McpServers.Enable();
        Assert.NotNull(McpConnectors.Connector);
    }
}

/// <summary>A fake MCP connection for unit-testing tool wrapping and listing.</summary>
sealed class FakeMcpConnection : IMcpConnection
{
    private readonly IReadOnlyList<McpToolInfo> _tools;

    public string? LastName { get; private set; }
    public string? LastArgs { get; private set; }

    public FakeMcpConnection(IReadOnlyList<McpToolInfo>? tools = null)
        => _tools = tools ?? new McpToolInfo[] { new("echo", "repite", "{}") };

    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct = default)
        => Task.FromResult(_tools);

    public Task<string> CallToolAsync(string name, string argumentsJson, CancellationToken ct = default)
    {
        LastName = name;
        LastArgs = argumentsJson;
        return Task.FromResult($"result:{name}");
    }

    public void Dispose() { }
}
