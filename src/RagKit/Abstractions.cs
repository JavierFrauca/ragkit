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

/// <summary>Which kind of event an <see cref="AgentStreamEvent"/> carries.</summary>
public enum AgentStreamEventKind
{
    /// <summary>The agent started calling a tool (<see cref="AgentStreamEvent.ToolName"/> is set).</summary>
    ToolCallStarted,
    /// <summary>A tool call finished (<see cref="AgentStreamEvent.ToolName"/> and
    /// <see cref="AgentStreamEvent.ToolResultSummary"/> are set).</summary>
    ToolCallFinished,
    /// <summary>The citations gathered so far (<see cref="AgentStreamEvent.Citations"/> is set) —
    /// emitted once, right before the first <see cref="Token"/> of the final answer.</summary>
    Citations,
    /// <summary>One piece of the final answer (<see cref="AgentStreamEvent.Token"/> is set).</summary>
    Token,
}

/// <summary>One event in an agentic stream — either tool-calling activity or a piece
/// of the final answer. Only the fields relevant to <see cref="Kind"/> are set.</summary>
public sealed record AgentStreamEvent(
    AgentStreamEventKind Kind,
    string? Token = null,
    string? ToolName = null,
    string? ToolResultSummary = null,
    IReadOnlyList<Citation>? Citations = null);

/// <summary>
/// A streamed agentic answer: a single event stream mixing tool-calling activity
/// with the final answer's tokens. Unlike <see cref="RagStream"/>, there's no eager
/// <c>Citations</c> field — in agent mode, citations accumulate as the model
/// decides to search (or not), so they arrive as their own <see
/// cref="AgentStreamEventKind.Citations"/> event, right before the first token,
/// rather than being known before the stream even starts.
/// </summary>
public sealed record AgentStream(IAsyncEnumerable<AgentStreamEvent> Events);

/// <summary>The three things ingestion can end in: new content was written
/// (<see cref="Ingested"/>), it was refused before writing anything
/// (<see cref="Rejected"/> — no domain defined, low classification confidence, or an
/// invalid explicit domain), or the content hash matched what's already stored so
/// nothing was re-written (<see cref="Unchanged"/>, see <c>IngestIfChangedAsync</c>).</summary>
public enum IngestOutcome { Ingested, Rejected, Unchanged }

/// <summary>What ingestion did: where the document landed and how it was split,
/// or why it didn't write new content (rejected, or unchanged since last ingest).</summary>
public sealed record IngestResult(
    string Source, string? Domain, IReadOnlyList<string> Labels, int ChunkCount,
    IngestOutcome Outcome = IngestOutcome.Ingested, string? Reason = null, double Confidence = 1.0)
{
    /// <summary>True when <see cref="Outcome"/> is <see cref="IngestOutcome.Rejected"/>.</summary>
    public bool Rejected => Outcome == IngestOutcome.Rejected;
}

/// <summary>A document as seen from the outside: one or more chunks sharing a
/// <see cref="Source"/>, aggregated for inventory purposes.</summary>
public sealed record DocumentInfo(string Source, string? Domain, int ChunkCount, DateTime IngestedAtUtc);

/// <summary>Result of <see cref="RagClient.RemoveDomainAsync"/>: whether the domain
/// actually existed (distinct from having existed with zero chunks) and how many
/// chunks were removed.</summary>
public sealed record DomainRemovalResult(bool Existed, int RemovedChunks);

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

    /// <summary>
    /// Realistic character budget for a chunk embedded with this model (its effective
    /// token window minus a safety margin, at ~4 chars/token) — used to size the
    /// structural chunker instead of a single value hardcoded for every backend.
    /// <see cref="RagOptions.ChunkMaxChars"/> overrides this when set. Default 1000
    /// (the old fixed-window <c>Chunker</c> default) — conservative, fits any small model.
    /// </summary>
    int MaxChunkChars => 1000;

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

/// <summary>Which kind of delta <see cref="NextStreamAsync"/> yielded.</summary>
public enum AgentDeltaKind
{
    /// <summary>A piece of the model's text answer (<see cref="AgentDelta.Content"/> is set).</summary>
    ContentPiece,
    /// <summary>A tool call's name just became known, before its arguments finish
    /// streaming (<see cref="AgentDelta.ToolName"/> is set) — lets a caller show
    /// "calling X…" before the full call is assembled.</summary>
    ToolCallStarted,
    /// <summary>The turn's tool calls, fully assembled (<see cref="AgentDelta.ToolCalls"/>
    /// is set) — emitted once, at the end of a turn that requested tools.</summary>
    ToolCallsReady,
}

/// <summary>One raw delta from a streamed agent turn (see <see cref="IChatClient.NextStreamAsync"/>).
/// Only the field matching <see cref="Kind"/> is set.</summary>
public sealed record AgentDelta(
    AgentDeltaKind Kind, string? Content = null,
    string? ToolName = null, IReadOnlyList<ToolCall>? ToolCalls = null);

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

    /// <summary>
    /// Streamed counterpart of <see cref="NextAsync"/>: yields the turn's content
    /// piece-by-piece, or its tool calls once fully assembled. Default implementation
    /// makes one non-streamed <see cref="NextAsync"/> call and yields its result as a
    /// single delta (so non-streaming clients still work); the OpenAI-compatible
    /// client overrides this to read the server's SSE stream.
    /// </summary>
    async IAsyncEnumerable<AgentDelta> NextStreamAsync(
        IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var turn = await NextAsync(messages, tools, ct).ConfigureAwait(false);
        if (turn.ToolCalls.Count > 0)
            yield return new AgentDelta(AgentDeltaKind.ToolCallsReady, ToolCalls: turn.ToolCalls);
        else if (!string.IsNullOrEmpty(turn.Content))
            yield return new AgentDelta(AgentDeltaKind.ContentPiece, Content: turn.Content);
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

/// <summary>
/// Which tools <see cref="RagClient.AskAgentAsync(string,string?,int,AgentToolScope,CancellationToken)"/>
/// offers the model. <see cref="SearchOnly"/> (the flags default value) always includes
/// <c>search_knowledge_base</c> — it can't be turned off. Combine the other flags to widen
/// the surface; <see cref="All"/> reproduces the pre-scoping behavior (every internal tool
/// plus any registered external ones).
/// </summary>
[Flags]
public enum AgentToolScope
{
    /// <summary>Only <c>search_knowledge_base</c> — safe for public/unauthenticated callers.</summary>
    SearchOnly = 0,

    /// <summary>Read-only structure tools: <c>list_domains</c>, <c>list_labels</c>.</summary>
    Classification = 1 << 0,

    /// <summary>State-mutating tools: create domain/label/profile/guardrail, ingest a document.</summary>
    Mutation = 1 << 1,

    /// <summary>Externally registered tools (e.g. MCP connectors) via <see cref="RagClient.RegisterTool"/>.</summary>
    External = 1 << 2,

    /// <summary>Lets <c>search_knowledge_base</c> take an explicit <c>domain</c> argument per
    /// call, narrowing to it, or search every domain when the argument is omitted. Without this
    /// flag the tool stays locked to the turn's routed domain no matter what the model passes —
    /// this flag is what lets it deliberately escape that once it decides routing picked wrong.</summary>
    CrossDomainSearch = 1 << 3,

    /// <summary>Every tool — the behavior before this scope existed. Not safe for untrusted callers.</summary>
    All = Classification | Mutation | External | CrossDomainSearch,
}
