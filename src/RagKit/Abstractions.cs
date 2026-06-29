using System.Runtime.CompilerServices;

namespace RagKit;

/// <summary>A domain (namespace) with a description the tier-2 model uses to route documents.</summary>
public sealed record DomainInfo(string Name, string Description = "");

/// <summary>A label in the vocabulary, with a description to guide auto-labeling.</summary>
public sealed record LabelInfo(string Name, string Description = "");

/// <summary>
/// A profile: a "lens"/sub-view inside a domain (e.g. architect, plumber,
/// electrician within "construction"). The tier-2 model can select it from the
/// query text. It carries (a) a focused system <see cref="Prompt"/> and (b) the
/// <see cref="Labels"/> that scope retrieval to that lens. Both are optional:
/// with no prompt the resolution chain falls back to the domain/global prompt,
/// with no labels retrieval isn't narrowed.
/// </summary>
public sealed record ProfileInfo(
    string Name, string Domain, string Description = "",
    string? Prompt = null, IReadOnlyList<string>? Labels = null);

/// <summary>Where a guardrail rule applies: the incoming query, or the generated answer.</summary>
public enum GuardrailStage { Input, Output }

/// <summary>
/// A guardrail rule in natural language, interpreted by the tier-2 model as a
/// system-level instruction (never concatenated with — and so never overridable
/// by — the user input). Scope is optional: a null <see cref="Domain"/>/
/// <see cref="Profile"/> makes the rule global / domain-wide.
/// </summary>
public sealed record GuardrailRule(
    string Description, GuardrailStage Stage = GuardrailStage.Input,
    string? Domain = null, string? Profile = null);

/// <summary>
/// The outcome of routing a query: the chosen domain, zero or more profiles
/// (multi when overlapping branches apply), the labels those profiles map to,
/// and the model's confidence in the domain choice.
/// </summary>
public sealed record RouteDecision(
    string? Domain, IReadOnlyList<string> Profiles,
    IReadOnlyList<string> Labels, double Confidence);

/// <summary>A guardrail verdict: whether the content is allowed, and why not if blocked.</summary>
public sealed record GuardDecision(bool Allowed, string? Reason);

/// <summary>A source-attributed excerpt that supported the answer.</summary>
public sealed record Citation(string Source, string Snippet, double Score);

/// <summary>The answer plus the citations it was grounded on.</summary>
public sealed record RagAnswer(string Answer, IReadOnlyList<Citation> Citations);

/// <summary>
/// A streaming answer: the <see cref="Citations"/> are known up front (retrieval
/// runs before generation) and <see cref="Tokens"/> yields the answer text as it
/// arrives. Enumerate <see cref="Tokens"/> to consume the stream.
/// </summary>
public sealed record RagStream(IReadOnlyList<Citation> Citations, IAsyncEnumerable<string> Tokens);

/// <summary>What ingestion did: where the document landed and how it was split,
/// or why it was rejected (no domains defined, or low classification confidence).</summary>
public sealed record IngestResult(
    string Source, string? Domain, IReadOnlyList<string> Labels, int ChunkCount,
    bool Rejected = false, string? Reason = null, double Confidence = 1.0);

/// <summary>One chat message (role = "system" | "user" | "assistant").</summary>
public readonly record struct ChatMessage(string Role, string Content);

/// <summary>
/// Turns text into a vector. The default implementation runs locally; advanced
/// users can inject their own (e.g. ONNX with a specific model, or a hosted API).
/// <see cref="ModelId"/> and <see cref="Dimension"/> are recorded by the store so
/// it can refuse to reopen a collection with a different embedding.
/// </summary>
public interface IEmbedder
{
    /// <summary>Stable id of the embedding model (e.g. "onnx:multilingual-e5-small").</summary>
    string ModelId { get; }

    /// <summary>Output vector dimension. Valid after <see cref="InitializeAsync"/>.</summary>
    int Dimension { get; }

    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// One-time async initialization (probe the dimension from a remote endpoint,
    /// load a model from disk…). <see cref="RagClient.CreateAsync"/> awaits this
    /// once, before the store's model/dimension guard runs — so embedders never
    /// block on async work in their constructor. Implementations that compute
    /// <see cref="Dimension"/> lazily must set it here before returning.
    /// Default: nothing.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Embed many texts at once. The default loops over <see cref="EmbedAsync"/>;
    /// backends that support real batching — an API array call, parallel local
    /// inference — override this for throughput on bulk ingest. Output is aligned
    /// with the input order.
    /// </summary>
    async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var result = new float[texts.Count][];
        for (int i = 0; i < texts.Count; i++)
            result[i] = await EmbedAsync(texts[i], ct).ConfigureAwait(false);
        return result;
    }
}

/// <summary>A tool offered to the model (function-calling): name + description + JSON-Schema params.</summary>
public sealed record ToolSpec(string Name, string Description, string ParametersSchema);

/// <summary>A tool invocation requested by the model.</summary>
public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>One step of the agent loop: either final text, or tool calls to run.</summary>
public sealed record AgentTurn(string? Content, IReadOnlyList<ToolCall> ToolCalls);

/// <summary>A message in the agent loop (adds tool role + tool-call metadata to <see cref="ChatMessage"/>).</summary>
public sealed record AgentMessage(string Role, string? Content, string? ToolCallId = null, IReadOnlyList<ToolCall>? ToolCalls = null);

/// <summary>
/// Talks to a chat model. The default implementation speaks the OpenAI-compatible
/// `/chat/completions` protocol; inject your own to use a different transport.
/// </summary>
public interface IChatClient
{
    Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default);

    /// <summary>
    /// Stream the answer token-by-token. The default implementation yields the full
    /// completion as a single chunk (so non-streaming clients still work); the
    /// OpenAI-compatible client overrides this to read the server's SSE stream.
    /// </summary>
    async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return await CompleteAsync(messages, ct).ConfigureAwait(false);
    }

    /// <summary>Whether this client/model supports native tool-calling. Default: no.</summary>
    bool SupportsTools => false;

    /// <summary>
    /// One agent turn with tools. Default implementation ignores tools and returns
    /// a plain completion (so non-tool clients still work), letting the agent loop
    /// fall back to one-shot.
    /// </summary>
    async Task<AgentTurn> NextAsync(IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools, CancellationToken ct = default)
    {
        var simple = messages
            .Where(m => m.Role is "system" or "user" or "assistant")
            .Select(m => new ChatMessage(m.Role, m.Content ?? ""))
            .ToList();
        var text = await CompleteAsync(simple, ct).ConfigureAwait(false);
        return new AgentTurn(text, Array.Empty<ToolCall>());
    }
}

/// <summary>
/// Optional second-stage reranker: re-scores the top fused candidates against the
/// query (e.g. a cross-encoder) before the final top-k. Pluggable; none by default.
/// </summary>
public interface IReranker
{
    Task<IReadOnlyList<StoredHit>> RerankAsync(string query, IReadOnlyList<StoredHit> candidates, int topK, CancellationToken ct = default);
}

/// <summary>A tool the agent can call to read or modify the knowledge base.</summary>
public interface IRagTool
{
    string Name { get; }
    string Description { get; }
    /// <summary>JSON-Schema (object) describing the parameters.</summary>
    string ParametersSchema { get; }
    Task<string> InvokeAsync(string argumentsJson, CancellationToken ct = default);
}
