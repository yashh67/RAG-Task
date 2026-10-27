using System.Text.Json.Serialization;

namespace ChatBot.Models;

public enum DocumentStatus
{
    Indexing,
    Ready,
    Failed
}

/// <summary>A document in docs/. Shared by all sessions; updated by the background indexer.</summary>
public sealed class DocumentInfo
{
    public DocumentInfo(string id, string fileName, string filePath)
    {
        Id = id;
        FileName = fileName;
        FilePath = filePath;
        Title = Path.GetFileNameWithoutExtension(fileName);
    }

    /// <summary>SHA-256 of the file (hex). Also the cache file name.</summary>
    public string Id { get; }
    public string FileName { get; }
    public string FilePath { get; }

    /// <summary>Ships with the app (Rag:BuiltInDocuments); can't be deleted from the UI.</summary>
    public bool IsBuiltIn { get; init; }
    public string Title { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Indexing;
    public double Progress { get; set; }

    /// <summary>PDF pages, or paragraphs for DOCX/TXT/MD.</summary>
    public int PageCount { get; set; }
    public bool HasPages { get; set; }
    public int ChunkCount { get; set; }
    public string? Warning { get; set; }
    public string? Error { get; set; }
}

/// <summary>A piece of a document with its embedding. Stored in data/index/{hash}.json.</summary>
public sealed class DocumentChunk
{
    public string DocumentId { get; set; } = "";
    public string Document { get; set; } = "";
    public string Section { get; set; } = "";

    /// <summary>Page labels as printed in the document (or paragraph numbers for files without pages).</summary>
    public string PageStart { get; set; } = "";
    public string PageEnd { get; set; } = "";
    public bool HasPages { get; set; }
    public string Text { get; set; } = "";
    public float[] Embedding { get; set; } = [];

    // Keyword index data, rebuilt when loaded.
    [JsonIgnore] public Dictionary<string, int> Terms { get; set; } = new();
    [JsonIgnore] public int TermCount { get; set; }

    /// <summary>Almost no text (e.g. a chart title with just "$(000's)"); left out of search results.</summary>
    [JsonIgnore] public bool IsTiny { get; set; }

    /// <summary>"p. 12", "pp. 12–13", or "¶ 4–6" for documents without pages.</summary>
    [JsonIgnore]
    public string Location
    {
        get
        {
            var range = PageStart == PageEnd ? PageStart : $"{PageStart}–{PageEnd}";
            if (!HasPages)
                return $"¶ {range}";
            if (PageStart.StartsWith("PDF ", StringComparison.Ordinal))
                return PageStart == PageEnd ? $"PDF p. {PageStart[4..]}" : $"PDF pp. {PageStart[4..]}–{PageEnd.Replace("PDF ", "")}";
            return PageStart == PageEnd ? $"p. {range}" : $"pp. {range}";
        }
    }
}
