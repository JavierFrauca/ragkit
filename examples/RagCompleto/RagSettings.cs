namespace RagCompleto;

/// <summary>Binds the "Rag" section of appsettings.json — LLM/embedder endpoint
/// configuration lives in config, not in C# constants.</summary>
public sealed class RagSettings
{
    public LlmSettings Answer { get; set; } = new();
    public EmbedderSettings Embedder { get; set; } = new();
}

public sealed class LlmSettings
{
    public string Url { get; set; } = "http://localhost:11434/v1";
    public string Model { get; set; } = "qwen2.5:7b";
}

public sealed class EmbedderSettings
{
    public string Url { get; set; } = "http://localhost:11434/v1";
    public string Model { get; set; } = "nomic-embed-text";
    public int Dimension { get; set; } = 768;
}

/// <summary>The three domains this example auto-classifies documents/questions into.</summary>
public static class Domains
{
    public const string Fiscal = "fiscal";
    public const string Rrhh = "rrhh";
    public const string Legal = "legal";

    public static readonly (string Name, string Description)[] All =
    {
        (Fiscal, "impuestos, IVA, IRPF, tributos"),
        (Rrhh, "personal, nóminas, contratos laborales"),
        (Legal, "contratos, normativa, cumplimiento"),
    };
}
