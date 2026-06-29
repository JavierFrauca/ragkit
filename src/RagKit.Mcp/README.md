# RagKit.Mcp

Cliente **MCP (Model Context Protocol)** por stdio para
[RagKit](https://www.nuget.org/packages/RagKit): conecta servidores MCP externos y
registra sus herramientas en el mismo bucle de agente. Cliente JSON-RPC propio, sin SDK.

```csharp
await rag.AddStdioServerAsync("npx", "-y", "@modelcontextprotocol/server-everything", "stdio");
// ahora AskAgentAsync también puede usar las tools del servidor MCP
```

Forma parte de RagKit — RAG agéntico llave en mano para .NET. Documentación completa
en el [repositorio](https://github.com/JavierFrauca/ragkit).
