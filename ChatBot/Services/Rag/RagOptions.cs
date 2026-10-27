namespace ChatBot.Services.Rag;

public sealed class RagOptions
{
    public string EmbeddingModelPath { get; set; } = "models/Qwen3-Embedding-0.6B-Q8_0.gguf";

    /// <summary>Prepended to search queries (Qwen3-Embedding's instruction format). Documents get <see cref="DocumentPrefix"/>.</summary>
    public string QueryPrefix { get; set; } =
        "Instruct: Given a question, retrieve passages from the documents that answer the question\nQuery:";
    public string DocumentPrefix { get; set; } = "";

    /// <summary>Append the model's end-of-text token to every input (needed by Qwen3-Embedding's last-token pooling).</summary>
    public bool AppendEndOfText { get; set; } = true;

    public string DocsFolder { get; set; } = "docs";

    /// <summary>
    /// File names in <see cref="DocsFolder"/> that ship with the app. They are always indexed at
    /// startup and can't be deleted from the UI.
    /// </summary>
    public string[] BuiltInDocuments { get; set; } = [];
    public string IndexFolder { get; set; } = "data/index";
    public int MaxUploadMB { get; set; } = 20;

    public int ChunkTokens { get; set; } = 400;
    public int ChunkOverlapTokens { get; set; } = 60;

    public int VectorTopK { get; set; } = 10;
    public int KeywordTopK { get; set; } = 10;
    public int FinalTopK { get; set; } = 5;
    public int ContextTokens { get; set; } = 2500;

    public int RewriteTurns { get; set; } = 3;
    public float RewriteTemperature { get; set; } = 0.1f;
}
