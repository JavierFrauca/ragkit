namespace RagKit;

/// <summary>
/// Registry hook for connecting the MCP servers declared in <see cref="RagOptions.Mcps"/>.
/// The core has no MCP dependency; the <c>RagKit.Mcp</c> package registers a connector
/// via <see cref="Register"/> (from its <c>McpServers.Enable()</c>), and
/// <see cref="RagClient.CreateAsync"/> invokes it once per configured endpoint at startup.
/// </summary>
public static class McpConnectors
{
    internal static Func<RagClient, string, CancellationToken, Task>? Connector;

    /// <summary>Register the MCP connector. Called by <c>RagKit.Mcp</c>'s <c>McpServers.Enable()</c>.</summary>
    public static void Register(Func<RagClient, string, CancellationToken, Task> connector) => Connector = connector;
}
