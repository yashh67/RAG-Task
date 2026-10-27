using System.Collections.Concurrent;
using ChatBot.Models;

namespace ChatBot.Services;

/// <summary>In-memory chat sessions. Everything is lost when the app restarts.</summary>
public sealed class SessionStore : IDisposable
{
    private static readonly TimeSpan IdleLimit = TimeSpan.FromHours(2);

    private readonly ConcurrentDictionary<Guid, ChatSession> _sessions = new();
    private readonly Timer _cleanupTimer;
    private readonly ILogger<SessionStore> _logger;

    public SessionStore(ILogger<SessionStore> logger)
    {
        _logger = logger;
        _cleanupTimer = new Timer(_ => RemoveIdleSessions(), null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
    }

    /// <summary>Raised with the user ID whose session list changed (created, deleted, renamed, new message).</summary>
    public event Action<string>? Changed;

    public ChatSession Create(string userId)
    {
        var session = new ChatSession(userId);
        _sessions[session.Id] = session;
        Changed?.Invoke(userId);
        return session;
    }

    /// <summary>Returns the session only if it belongs to the given user.</summary>
    public ChatSession? Get(Guid id, string userId) =>
        _sessions.TryGetValue(id, out var session) && session.UserId == userId ? session : null;

    public IReadOnlyList<ChatSession> List(string userId) =>
        _sessions.Values
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();

    public bool Delete(Guid id, string userId)
    {
        if (Get(id, userId) is null || !_sessions.TryRemove(id, out _))
            return false;

        Changed?.Invoke(userId);
        return true;
    }

    public IReadOnlyList<ChatMessage> GetMessages(ChatSession session)
    {
        lock (session.SyncRoot)
            return session.Messages.ToList();
    }

    public void AddMessage(ChatSession session, ChatMessage message)
    {
        lock (session.SyncRoot)
        {
            session.Messages.Add(message);
            session.UpdatedAt = DateTime.UtcNow;

            if (message.Role == ChatRole.User && session.Title == ChatSession.DefaultTitle)
                session.Title = MakeTitle(message.Content);
        }

        Changed?.Invoke(session.UserId);
    }

    private static string MakeTitle(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var title = string.Join(' ', words.Take(6));
        return words.Length > 6 ? title + "…" : title;
    }

    private void RemoveIdleSessions()
    {
        var cutoff = DateTime.UtcNow - IdleLimit;
        foreach (var session in _sessions.Values.Where(s => s.UpdatedAt < cutoff))
        {
            if (_sessions.TryRemove(session.Id, out _))
                Changed?.Invoke(session.UserId);
        }
        _logger.LogDebug("Session cleanup done, {Count} sessions in memory", _sessions.Count);
    }

    public void Dispose() => _cleanupTimer.Dispose();
}
