using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using Microsoft.Extensions.Options;

namespace ChatBot.Services.Rag;

/// <summary>
/// Turns text into L2-normalized vectors with a small embedding model (Qwen3-Embedding-0.6B by
/// default). Separate from the chat model, with its own lock, so indexing never blocks a reply
/// for longer than one chunk.
/// </summary>
public sealed class EmbeddingService : IDisposable
{
    private const int MaxInputTokens = 2048;

    private readonly RagOptions _options;
    private readonly ILogger<EmbeddingService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LLamaWeights? _weights;
    private readonly LLamaEmbedder? _embedder;
    private readonly string? _eosText;

    public EmbeddingService(IOptions<RagOptions> options, IOptions<LlmOptions> llmOptions, IHostEnvironment env, ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _logger = loggerFactory.CreateLogger<EmbeddingService>();

        var path = AppPaths.FindFile(_options.EmbeddingModelPath, env.ContentRootPath);
        if (path is null)
        {
            LoadError = $"Embedding model not found: {_options.EmbeddingModelPath}\nDownload it with:\n" +
                "curl -L -o models/Qwen3-Embedding-0.6B-Q8_0.gguf https://huggingface.co/Qwen/Qwen3-Embedding-0.6B-GGUF/resolve/main/Qwen3-Embedding-0.6B-Q8_0.gguf";
            _logger.LogError("{Message}", LoadError);
            return;
        }

        try
        {
            var threads = llmOptions.Value.Threads > 0 ? llmOptions.Value.Threads : LlmService.PhysicalCoreCount();
            var modelParams = new ModelParams(path)
            {
                ContextSize = MaxInputTokens,
                BatchSize = MaxInputTokens,     // the whole input must fit in one batch for pooling
                UBatchSize = MaxInputTokens,
                Embeddings = true,
                GpuLayerCount = 0,
                Threads = threads,
                BatchThreads = threads,
            };

            _logger.LogInformation("Loading embedding model {Model}...", path);
            _weights = LLamaWeights.LoadFromFile(modelParams);
            _embedder = new LLamaEmbedder(_weights, modelParams);
            ModelFingerprint = $"{Path.GetFileName(path)}:{new FileInfo(path).Length}";

            // Qwen3-Embedding pools the last token and is trained with <|endoftext|> at the end.
            // Add it ourselves unless the tokenizer already appends it.
            var eos = _weights.Vocab.EOS;
            if (eos is not null && !_weights.Vocab.ShouldAddEOS && options.Value.AppendEndOfText)
                _eosText = _weights.Vocab.LLamaTokenToString(eos.Value, true);
            _logger.LogInformation("Embedding model loaded ({Size} dimensions, appends end-of-text: {Eos}).",
                _weights.EmbeddingSize, _eosText is not null);
        }
        catch (Exception ex)
        {
            LoadError = $"Failed to load embedding model '{path}': {ex.Message}";
            _logger.LogError(ex, "Failed to load embedding model");
        }
    }

    public bool IsReady => _embedder is not null;
    public string? LoadError { get; }

    /// <summary>Identifies the model in cache files, so a different model never reuses old vectors.</summary>
    public string ModelFingerprint { get; } = "";

    public int CountTokens(string text) =>
        _weights?.Tokenize(text, false, false, Encoding.UTF8).Length ?? (text.Length + 3) / 4;

    public Task<float[]> EmbedQueryAsync(string query, CancellationToken ct) => EmbedAsync(_options.QueryPrefix + query, ct);

    public Task<float[]> EmbedDocumentAsync(string text, CancellationToken ct) => EmbedAsync(_options.DocumentPrefix + text, ct);

    private async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        if (_embedder is null)
            throw new InvalidOperationException(LoadError);

        text = Truncate(text);
        if (_eosText is not null)
            text += _eosText;

        await _gate.WaitAsync(ct);
        try
        {
            // CPU-bound native call: keep it off the caller's thread.
            var vectors = await Task.Run(() => _embedder.GetEmbeddings(text, ct), ct);
            return Normalize(vectors.Count == 1 ? vectors[0] : Mean(vectors));
        }
        finally
        {
            _gate.Release();
        }
    }

    private string Truncate(string text)
    {
        var limit = MaxInputTokens - 8;
        if (CountTokens(text) <= limit)
            return text;
        // Rare (very long queries): cut by characters until it fits.
        var length = text.Length * limit / CountTokens(text);
        while (length > 0 && CountTokens(text[..length]) > limit)
            length = length * 9 / 10;
        return text[..length];
    }

    private static float[] Mean(IReadOnlyList<float[]> vectors)
    {
        var mean = new float[vectors[0].Length];
        foreach (var v in vectors)
            for (var i = 0; i < mean.Length; i++)
                mean[i] += v[i] / vectors.Count;
        return mean;
    }

    public static float[] Normalize(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += x * x;
        var norm = (float)Math.Sqrt(sum);
        if (norm > 0)
            for (var i = 0; i < v.Length; i++) v[i] /= norm;
        return v;
    }

    public void Dispose()
    {
        _embedder?.Dispose();
        _weights?.Dispose();
        _gate.Dispose();
    }
}
