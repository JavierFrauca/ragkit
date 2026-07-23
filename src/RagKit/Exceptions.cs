using System;

namespace RagKit;

/// <summary>Base exception for all RagKit errors. Not sealed — specific subtypes inherit.</summary>
public class RagKitException : Exception
{
    public RagKitException(string message) : base(message) { }
    public RagKitException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>
/// Thrown when an LLM (tier-1 or tier-2) returns an HTTP error (4xx/5xx), times
/// out, or the endpoint is not configured. Recoverable — the caller can retry.
/// </summary>
public sealed class LlmException : RagKitException
{
    public int StatusCode { get; }
    public LlmException(string message, int statusCode = 0) : base(message) => StatusCode = statusCode;
}

/// <summary>
/// Thrown when a vector store backend (Qdrant, Postgres, SQL Server) fails with
/// an HTTP/database error.
/// </summary>
public sealed class StoreException : RagKitException
{
    public string StoreKind { get; }
    public StoreException(string message, string storeKind = "") : base(message) => StoreKind = storeKind;
}

/// <summary>
/// Thrown when the tier-2 classifier model returns unparseable or malformed JSON.
/// </summary>
public sealed class ClassificationException : RagKitException
{
    public ClassificationException(string message) : base(message) { }
}

/// <summary>
/// Thrown when the Markdig-based chunker encounters an unrecoverable parsing error.
/// </summary>
public sealed class ChunkingException : RagKitException
{
    public ChunkingException(string message, Exception? inner = null) : base(message, inner) { }
}
