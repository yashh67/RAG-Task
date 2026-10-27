using System.Text;
using ChatBot.Models;
using ChatBot.Services.Rag;
using Microsoft.Extensions.Options;

namespace ChatBot.Services;

/// <summary>Builds Qwen3 ChatML prompts and trims old turns so the prompt fits the context window.</summary>
public sealed class PromptBuilder
{
    // Assistant answers are long; for the rewrite step their start is enough to resolve references.
    private const int RewriteAnswerChars = 600;

    private readonly LlmService _llm;
    private readonly LlmOptions _options;
    private readonly RagOptions _ragOptions;
    private readonly string _systemPrompt;
    private readonly string _ragSystemPrompt;
    private readonly string _rewritePrompt;

    public PromptBuilder(LlmService llm, IOptions<LlmOptions> options, IOptions<RagOptions> ragOptions)
    {
        _llm = llm;
        _options = options.Value;
        _ragOptions = ragOptions.Value;
        _systemPrompt = ReadPrompt("system-prompt.txt");
        _ragSystemPrompt = ReadPrompt("rag-system-prompt.txt");
        _rewritePrompt = ReadPrompt("rewrite-prompt.txt");
    }

    private int PromptBudget => _options.ContextSize - _options.MaxResponseTokens;

    /// <param name="history">Earlier messages of this session only (not including the new message).</param>
    public string Build(IReadOnlyList<ChatMessage> history, string newUserMessage)
    {
        var turns = SplitIntoTurns(history);

        // Drop the oldest whole turns until the prompt plus the reply budget fits.
        for (var skip = 0; skip <= turns.Count; skip++)
        {
            var prompt = Render(_systemPrompt, turns.Skip(skip).SelectMany(t => t), newUserMessage);
            if (_llm.CountTokens(prompt) <= PromptBudget)
                return prompt;
        }

        throw new InvalidOperationException("Your message is too long for the model's context window. Please shorten it.");
    }

    /// <summary>
    /// Builds the document-answering prompt. Excerpts are limited to ContextTokens; if the whole
    /// prompt is still too long, old history goes first, then the lowest-ranked excerpts.
    /// </summary>
    /// <returns>The prompt and the excerpts actually included (numbered 1..n in that order).</returns>
    public (string Prompt, IReadOnlyList<DocumentChunk> Used) BuildRag(
        IReadOnlyList<ChatMessage> history, string question, IReadOnlyList<DocumentChunk> ranked)
    {
        var excerpts = new List<DocumentChunk>();
        var excerptTokens = 0;
        foreach (var chunk in ranked)
        {
            var tokens = _llm.CountTokens(FormatExcerpt(excerpts.Count + 1, chunk));
            if (excerptTokens + tokens > _ragOptions.ContextTokens)
                continue; // try the next (maybe shorter) one
            excerpts.Add(chunk);
            excerptTokens += tokens;
        }

        var turns = SplitIntoTurns(history);
        while (true)
        {
            for (var skip = 0; skip <= turns.Count; skip++)
            {
                var prompt = Render(_ragSystemPrompt, turns.Skip(skip).SelectMany(t => t), FormatRagTurn(excerpts, question));
                if (_llm.CountTokens(prompt) <= PromptBudget)
                    return (prompt, excerpts);
            }
            if (excerpts.Count == 0)
                throw new InvalidOperationException("Your message is too long for the model's context window. Please shorten it.");
            excerpts.RemoveAt(excerpts.Count - 1);
        }
    }

    /// <summary>Prompt asking the model to rewrite a follow-up as a standalone question.</summary>
    public string BuildRewrite(IReadOnlyList<ChatMessage> history, string message)
    {
        var recent = SplitIntoTurns(history).TakeLast(_ragOptions.RewriteTurns).SelectMany(t => t);
        var sb = new StringBuilder("Conversation:\n");
        foreach (var m in recent)
        {
            var text = m.Role == ChatRole.Assistant && m.Content.Length > RewriteAnswerChars
                ? m.Content[..RewriteAnswerChars] + "…"
                : m.Content;
            sb.Append(m.Role == ChatRole.User ? "User: " : "Assistant: ").Append(text).Append('\n');
        }
        sb.Append("\nLatest message: ").Append(message).Append("\n\nStandalone question:");
        return Render(_rewritePrompt, [], sb.ToString());
    }

    public static string FormatExcerpt(int number, DocumentChunk chunk) =>
        $"[{number}] ({chunk.Document}, {chunk.Section}, {chunk.Location})\n{chunk.Text}";

    private static string FormatRagTurn(List<DocumentChunk> excerpts, string question)
    {
        var sb = new StringBuilder("CONTEXT:\n");
        if (excerpts.Count == 0)
            sb.Append("(no matching excerpts were found)\n");
        for (var i = 0; i < excerpts.Count; i++)
            sb.Append(FormatExcerpt(i + 1, excerpts[i])).Append("\n\n");
        sb.Append("QUESTION: ").Append(question);
        return sb.ToString();
    }

    /// <summary>A turn is a user message plus the assistant message(s) that follow it.</summary>
    private static List<List<ChatMessage>> SplitIntoTurns(IReadOnlyList<ChatMessage> history)
    {
        var turns = new List<List<ChatMessage>>();
        foreach (var message in history)
        {
            if (message.Role == ChatRole.User || turns.Count == 0)
                turns.Add(new List<ChatMessage>());
            turns[^1].Add(message);
        }
        return turns;
    }

    private string Render(string systemPrompt, IEnumerable<ChatMessage> history, string newUserMessage)
    {
        var sb = new StringBuilder();
        Append(sb, "system", systemPrompt);
        foreach (var message in history)
            Append(sb, message.Role == ChatRole.User ? "user" : "assistant", message.Content);
        Append(sb, "user", newUserMessage);

        sb.Append("<|im_start|>assistant\n");
        if (!_options.EnableThinking)
            sb.Append("<think>\n\n</think>\n\n"); // Qwen3's official way to switch thinking off
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string role, string content) =>
        sb.Append("<|im_start|>").Append(role).Append('\n')
          .Append(content).Append(LlmService.EndOfTurn).Append('\n');

    private static string ReadPrompt(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Prompts", fileName)).Trim();
}
