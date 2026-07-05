# RagKit.Onnx

Embeddings **ONNX locales** para [RagKit](https://www.nuget.org/packages/RagKit):
modelos sentence-transformers in-process — mean-pooling + L2. Semántico y privado, sin
llamadas a la nube. Tokenizer **WordPiece** (`vocab.txt`) o **SentencePiece**
(`sentencepiece.bpe.model`/`spiece.model`/`tokenizer.model`, para modelos multilingües
como multilingual-e5). Incluye además un **reranker cross-encoder** local
(`OnnxCrossEncoderReranker`, vía `rag.SetReranker(...)`).

```csharp
using RagKit.Onnx;

// Zero-config: descarga y cachea el modelo la primera vez.
var opts = new RagOptions
{
    Embedder = await OnnxEmbedding.UseDefaultModelAsync(),              // all-MiniLM-L6-v2 (inglés)
    // Embedder = await OnnxEmbedding.UseMultilingualDefaultModelAsync(), // multilingual-e5-small (~100 idiomas, incl. español)
};
```

O apuntando a un modelo propio ya descargado:

```csharp
OnnxEmbedding.Enable();
var opts = new RagOptions {
    Embedder = new EmbedderConfig { Kind = EmbedderKind.Onnx, Model = "C:/models/all-MiniLM-L6-v2" }
    // la carpeta contiene model.onnx + vocab.txt (o sentencepiece.bpe.model)
};
```

Forma parte de RagKit — RAG agéntico llave en mano para .NET. Documentación completa
en el [repositorio](https://github.com/JavierFrauca/ragkit).
