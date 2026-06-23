namespace RagKit;

/// <summary>Store configuration (used by the factory). One enum switches backend.</summary>
public sealed class StoreConfig
{
    /// <summary>
    /// A ready store instance to use directly. When set, it wins over
    /// <see cref="Kind"/> and the factory — no enum, no global <c>Enable()</c>,
    /// just compile-time-checked injection of your own <see cref="IVectorStore"/>.
    /// </summary>
    public IVectorStore? Instance { get; set; }

    public VectorStoreKind Kind { get; set; } = VectorStoreKind.InMemory;
    /// <summary>For Qdrant: base URL (default http://127.0.0.1:6333).</summary>
    public string? Url { get; set; }
    public string? ApiKey { get; set; }
    /// <summary>Collection/table name (default "ragkit").</summary>
    public string Collection { get; set; } = "ragkit";
    /// <summary>For SQL Server / Postgres: the connection string.</summary>
    public string? ConnectionString { get; set; }
    /// <summary>For InMemory: where the catalog/guard file is persisted.</summary>
    public string DataPath { get; set; } = "./ragkit-data";
}

/// <summary>
/// Builds the configured <see cref="IVectorStore"/>. Backends register a builder
/// for their <see cref="VectorStoreKind"/>; InMemory and Qdrant are built in, and
/// connector packages (e.g. RagKit.Postgres) register themselves via
/// <see cref="Register"/>. This keeps the core free of heavy DB dependencies while
/// preserving the one-enum selector.
/// </summary>
public static class VectorStoreFactory
{
    private static readonly Dictionary<VectorStoreKind, Func<StoreConfig, IVectorStore>> Registry = new()
    {
        [VectorStoreKind.InMemory] = c => new InMemoryVectorStore(c.DataPath),
        [VectorStoreKind.Qdrant] = c => new QdrantVectorStore(c.Url ?? "http://127.0.0.1:6333", c.Collection, c.ApiKey),
    };

    /// <summary>Register (or replace) the builder for a backend. Called by connector packages.</summary>
    public static void Register(VectorStoreKind kind, Func<StoreConfig, IVectorStore> builder)
        => Registry[kind] = builder;

    public static IVectorStore Create(StoreConfig? config)
    {
        config ??= new StoreConfig();
        if (config.Instance is not null) return config.Instance;
        if (Registry.TryGetValue(config.Kind, out var builder)) return builder(config);
        throw new NotSupportedException(
            $"El conector '{config.Kind}' no está registrado. Referencia su paquete (p. ej. RagKit.Postgres) " +
            "y llama a su método Enable(), o regístralo con VectorStoreFactory.Register(...).");
    }
}
