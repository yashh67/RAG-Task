using System.Text;
using ChatBot.Models;
using ChatBot.Services.Rag;
using Microsoft.Extensions.Options;

namespace ChatBot.Services;

/// <summary>What the assistant is doing before text starts streaming.</summary>
public enum ChatStage
{
    WaitingForModel,
    Searching,
    Thinking,
}

/// <summary>Sends one user message in a session and stores the reply.</summary>
public sealed class ChatService
{
    private readonly SessionStore _store;
    private readonly PromptBuilder _prompts;
    private readonly LlmService _llm;
    private readonly Retriever _retriever;
    private readonly DocumentLibrary _library;
    private readonly RagOptions _ragOptions;
    private readonly ILogger<ChatService> _logger;

    public ChatService(SessionStore store, PromptBuilder prompts, LlmService llm, Retriever retriever,
        DocumentLibrary library, IOptions<RagOptions> ragOptions, ILogger<ChatService> logger)
    {
        _store = store;
        _prompts = prompts;
        _llm = llm;
        _retriever = retriever;
        _library = library;
        _ragOptions = ragOptions.Value;
        _logger = logger;
    }

    /// <param name="onStage">Called when the assistant starts waiting, searching or thinking.</param>
    /// <param name="onText">Called with the full reply text so far after every streamed piece.</param>
    /// <remarks>
    /// On cancellation (Stop) the partial reply is saved. On any other error no assistant message
    /// is saved and the exception propagates.
    /// </remarks>
    public async Task SendAsync(
        ChatSession session, string userText, Action<ChatStage> onStage, Action<string> onText, CancellationToken ct)
    {
        // History comes only from this session in the server-side store.
        var history = _store.GetMessages(session);
        _store.AddMessage(session, new ChatMessage(ChatRole.User, userText));

        var reply = new StringBuilder();
        IReadOnlyList<SourceRef> sources = [];
        IReadOnlyList<DocumentChunk> excerpts = [];
        try
        {
            // Run on the thread pool so CPU-heavy work never blocks the Blazor circuit.
            await Task.Run(async () =>
            {
                string prompt;
                if (session.UseDocuments)
                {
                    onStage(ChatStage.Searching);
                    var query = history.Count > 0 ? await RewriteAsync(history, userText, onStage, ct) : userText;
                    var documentId = session.DocumentId is { } id && _library.Get(id) is not null ? id : null;
                    var ranked = await _retriever.SearchAsync(query, documentId, ct);
                    (prompt, var used) = _prompts.BuildRag(history, userText, ranked);
                    excerpts = used;
                    sources = used.Select((c, i) => new SourceRef(i + 1, c.Document, c.Section, c.Location, Snippet(c.Text))).ToList();
                    _logger.LogInformation("RAG query \"{Query}\" -> {Count} excerpts ({Locations})",
                        query, used.Count, string.Join(", ", used.Select(c => c.Location)));
                }
                else
                {
                    prompt = _prompts.Build(history, userText);
                }

                if (_llm.IsBusy) onStage(ChatStage.WaitingForModel);
                await foreach (var piece in _llm.GenerateAsync(prompt, () => onStage(ChatStage.Thinking), ct))
                {
                    reply.Append(piece);
                    onText(CitationFormatter.Normalize(reply.ToString(), excerpts));
                }
            }, CancellationToken.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped by the user: keep whatever was generated.
        }

        var content = CitationFormatter.Normalize(reply.ToString(), excerpts).Trim();
        if (content.Length > 0)
            _store.AddMessage(session, new ChatMessage(ChatRole.Assistant, content, sources));
    }

    /// <summary>Rewrites a follow-up ("what about the second one?") as a standalone search question.</summary>
    private async Task<string> RewriteAsync(IReadOnlyList<ChatMessage> history, string message, Action<ChatStage> onStage, CancellationToken ct)
    {
        var prompt = _prompts.BuildRewrite(history, message);
        if (_llm.IsBusy) onStage(ChatStage.WaitingForModel);

        var sb = new StringBuilder();
        await foreach (var piece in _llm.GenerateAsync(prompt, () => onStage(ChatStage.Searching), ct,
                           temperature: _ragOptions.RewriteTemperature, maxTokens: 96))
            sb.Append(piece);

        var rewritten = sb.ToString().Trim().Split('\n')[0].Trim().Trim('"', '“', '”');
        if (rewritten.StartsWith("Standalone question:", StringComparison.OrdinalIgnoreCase))
            rewritten = rewritten["Standalone question:".Length..].Trim();
        return rewritten.Length is > 0 and < 400 ? rewritten : message;
    }

    private static string Snippet(string text)
    {
        if (text.Length <= 220)
            return text;
        var cut = text.LastIndexOf(' ', 220);
        return text[..(cut > 100 ? cut : 220)] + "…";
    }
}
