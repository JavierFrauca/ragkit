using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RagKit.Internal;

/// <summary>
/// Observability backbone: one ActivitySource for tracing and helpers to create
/// ILogger instances from the factory in RagOptions.
/// </summary>
internal static class Telemetry
{
    /// <summary>The source for all RagKit tracing spans.</summary>
    public static readonly ActivitySource Source = new("RagKit");

    /// <summary>Create a logger for the given type from the factory in options.
    /// Returns NullLogger when no factory is configured (the default).</summary>
    public static ILogger<T> GetLogger<T>(RagOptions options) =>
        (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger<T>();
}