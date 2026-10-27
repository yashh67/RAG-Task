namespace ChatBot.Models;

public enum ChatRole
{
    User,
    Assistant
}

public sealed class ChatMessage
{
    public ChatMessage(ChatRole role, string content, IReadOnlyList<SourceRef>? sources = null)
    {
        Role = role;
        Content = content;
        Sources = sources ?? [];
    }

    public ChatRole Role { get; }
    public string Content { get; }
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    /// <summary>Excerpts the answer was based on. Shown in the UI only, never sent back to the model.</summary>
    public IReadOnlyList<SourceRef> Sources { get; }
}

/// <summary>One numbered excerpt given to the model, as shown in the Sources list.</summary>
public sealed record SourceRef(int Number, string Document, string Section, string Location, string Snippet);
