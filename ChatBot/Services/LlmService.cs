using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Microsoft.Extensions.Options;

namespace ChatBot.Services;

public sealed class LlmOptions
{
    public string ModelPath { get; set; } = "models/Qwen3-4B-Q4_K_M.gguf";
    public int ContextSize { get; set; } = 8192;
    public int Threads { get; set; }
    public int MaxResponseTokens { get; set; } = 1024;
    public float Temperature { get; set; } = 0.7f;
    public float TopP { get; set; } = 0.8f;
    public int TopK { get; set; } = 20;
    public bool EnableThinking { get; set; }
}

/// <summary>
/// Owns the single loaded model. All inference goes through one semaphore, so only one
/// request uses the model at a time and the others wait their turn.
/// </summary>
public sealed class LlmService : IDisposable
{
    public const string EndOfTurn = "<|im_end|>";

    private readonly LlmOptions _options;
    private readonly ILogger<LlmService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LLamaWeights? _weights;
    private readonly StatelessExecutor? _executor;

    public LlmService(IOptions<LlmOptions> options, IHostEnvironment env, ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _logger = loggerFactory.CreateLogger<LlmService>();

        var modelPath = AppPaths.FindFile(_options.ModelPath, env.ContentRootPath);
        if (modelPath is null)
        {
            LoadError = $"Model file not found: {_options.ModelPath}\n{DownloadHint(Path.GetFileName(_options.ModelPath))}";
            _logger.LogError("{Message}", LoadError);
            return;
        }

        try
        {
            var threads = _options.Threads > 0 ? _options.Threads : PhysicalCoreCount();
            var modelParams = new ModelParams(modelPath)
            {
                ContextSize = (uint)_options.ContextSize,
                GpuLayerCount = 0,
                Threads = threads,
                BatchThreads = threads,
            };

            _logger.LogInformation("Loading {Model} with {Threads} threads, context {Context}...",
                modelPath, threads, _options.ContextSize);
            _weights = LLamaWeights.LoadFromFile(modelParams);

            // The executor is "stateless": every InferAsync call creates a fresh context from the
            // full prompt, so sessions never share KV cache. We reuse one instance because its
            // constructor allocates (and throws away) a full context, and calls are serialized anyway.
            _executor = new StatelessExecutor(_weights, modelParams) { ApplyTemplate = false };
            _logger.LogInformation("Model loaded.");
        }
        catch (Exception ex)
        {
            LoadError = $"Failed to load model '{modelPath}': {ex.Message}";
            _logger.LogError(ex, "Failed to load model");
        }
    }

    public bool IsReady => _executor is not null;
    public string? LoadError { get; }

    /// <summary>True while some request holds the model.</summary>
    public bool IsBusy => _gate.CurrentCount == 0;

    public int CountTokens(string text) =>
        _weights?.Tokenize(text, false, true, Encoding.UTF8).Length
        ?? throw new InvalidOperationException(LoadError);

    /// <summary>
    /// Streams the reply for a full ChatML prompt. <paramref name="onStarted"/> fires once this
    /// request gets the model (useful for replacing a "waiting" indicator).
    /// <paramref name="temperature"/> and <paramref name="maxTokens"/> override the configured values.
    /// </summary>
    public async IAsyncEnumerable<string> GenerateAsync(
        string prompt, Action? onStarted, [EnumeratorCancellation] CancellationToken ct,
        float? temperature = null, int? maxTokens = null)
    {
        if (_executor is null)
            throw new InvalidOperationException(LoadError);

        await _gate.WaitAsync(ct);
        try
        {
            onStarted?.Invoke();

            var inferenceParams = new InferenceParams
            {
                MaxTokens = maxTokens ?? _options.MaxResponseTokens,
                AntiPrompts = [EndOfTurn],
                // Needed so <think> and <|im_end|> come through as text and can be filtered out.
                DecodeSpecialTokens = true,
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = temperature ?? _options.Temperature,
                    TopP = _options.TopP,
                    TopK = _options.TopK,
                },
            };

            var filter = new OutputFilter();
            await foreach (var piece in _executor.InferAsync(prompt, inferenceParams, ct))
            {
                var text = filter.Push(piece);
                if (text.Length > 0)
                    yield return text;
                if (filter.Ended)
                    break;
            }

            var rest = filter.Flush();
            if (rest.Length > 0)
                yield return rest;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _weights?.Dispose();
        _gate.Dispose();
    }

    private static string DownloadHint(string fileName)
    {
        var repo = fileName switch
        {
            "Qwen3-4B-Q4_K_M.gguf" => "Qwen/Qwen3-4B-GGUF",
            "Qwen3-1.7B-Q4_K_M.gguf" => "unsloth/Qwen3-1.7B-GGUF",
            _ => null,
        };
        return repo is null
            ? "Download a Qwen3 GGUF file into the models/ folder and set Llm:ModelPath in appsettings.json."
            : $"Download it with:\ncurl -L -o models/{fileName} https://huggingface.co/{repo}/resolve/main/{fileName}";
    }

    internal static int PhysicalCoreCount()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                // RelationProcessorCore = 0: one variable-size record per physical core.
                uint length = 0;
                GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref length);
                var buffer = Marshal.AllocHGlobal((int)length);
                try
                {
                    if (GetLogicalProcessorInformationEx(0, buffer, ref length))
                    {
                        int cores = 0;
                        for (uint offset = 0; offset < length; cores++)
                            offset += (uint)Marshal.ReadInt32(buffer, (int)offset + 4); // record Size field
                        if (cores > 0)
                            return cores;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch
            {
                // fall through to the estimate below
            }
        }
        return Math.Max(1, Environment.ProcessorCount / 2);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);

    /// <summary>
    /// Removes &lt;think&gt;...&lt;/think&gt; blocks and stops at &lt;|im_end|&gt;. Tags can be split
    /// across tokens, so anything that might be the start of a tag is held back until it's decided.
    /// </summary>
    private sealed class OutputFilter
    {
        private const string ThinkOpen = "<think>";
        private const string ThinkClose = "</think>";

        private readonly StringBuilder _pending = new();
        private bool _inThink;

        public bool Ended { get; private set; }

        public string Push(string piece)
        {
            if (Ended)
                return "";
            _pending.Append(piece);
            return Drain(final: false);
        }

        public string Flush() => Ended ? "" : Drain(final: true);

        private string Drain(bool final)
        {
            var output = new StringBuilder();
            while (true)
            {
                var text = _pending.ToString();

                if (_inThink)
                {
                    var close = text.IndexOf(ThinkClose, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        // Drop thinking text, keeping just enough to catch a split closing tag.
                        var keep = final ? 0 : Math.Min(text.Length, ThinkClose.Length - 1);
                        _pending.Remove(0, text.Length - keep);
                        return output.ToString();
                    }
                    _pending.Remove(0, close + ThinkClose.Length);
                    _inThink = false;
                    continue;
                }

                var end = text.IndexOf(EndOfTurn, StringComparison.Ordinal);
                var open = text.IndexOf(ThinkOpen, StringComparison.Ordinal);

                if (end >= 0 && (open < 0 || end < open))
                {
                    output.Append(text, 0, end);
                    _pending.Clear();
                    Ended = true;
                    return output.ToString();
                }

                if (open >= 0)
                {
                    output.Append(text, 0, open);
                    _pending.Remove(0, open + ThinkOpen.Length);
                    _inThink = true;
                    continue;
                }

                var hold = final ? 0 : PossibleTagStartLength(text);
                output.Append(text, 0, text.Length - hold);
                _pending.Remove(0, text.Length - hold);
                return output.ToString();
            }
        }

        /// <summary>Length of the longest suffix of <paramref name="text"/> that could begin a tag.</summary>
        private static int PossibleTagStartLength(string text)
        {
            for (var len = Math.Min(text.Length, EndOfTurn.Length - 1); len > 0; len--)
            {
                var suffix = text.AsSpan(text.Length - len);
                if (ThinkOpen.AsSpan().StartsWith(suffix) || EndOfTurn.AsSpan().StartsWith(suffix))
                    return len;
            }
            return 0;
        }
    }
}
