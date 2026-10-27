namespace ChatBot.Models;

public sealed class ChatSession
{
    public const string DefaultTitle = "New chat";

    public ChatSession(string userId)
    {
        UserId = userId;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string UserId { get; }
    public string Title { get; set; } = DefaultTitle;
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>"Answer from documents" toggle.</summary>
    public bool UseDocuments { get; set; } = true;

    /// <summary>Search only this document (its ID), or all documents when null.</summary>
    public string? DocumentId { get; set; }

    // Only touch this while holding SyncRoot (SessionStore does that for you).
    public List<ChatMessage> Messages { get; } = new();

    internal object SyncRoot { get; } = new();
}
