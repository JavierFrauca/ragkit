namespace RagSimple;

/// <summary>Binds the "Rag" section of appsettings.json — LLM/embedder endpoint
/// configuration lives in config, not in C# constants, so swapping to a cloud
/// provider (DeepSeek/OpenAI) is a one-file edit, no rebuild of logic needed.</summary>
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

/// <summary>The one domain every document/question in this example falls into.</summary>
public static class Rag
{
    public const string Domain = "documentos";
}
