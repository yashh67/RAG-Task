using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using ChatBot.Models;
using Microsoft.Extensions.Options;

namespace ChatBot.Services.Rag;

/// <summary>
/// The shared document collection: files in docs/, their chunks and embeddings. Documents are
/// indexed one at a time in the background; results are cached in data/index/{hash}.json.
/// </summary>
public sealed class DocumentLibrary : BackgroundService
{
    /// <summary>Bump when parsing or chunking changes, so old cache files are rebuilt.</summary>
    private const int IndexVersion = 2;

    /// <summary>Save a checkpoint of a document being indexed after this many new chunks.</summary>
    private const int CheckpointEvery = 25;

    private sealed class Entry(DocumentInfo info)
    {
        public DocumentInfo Info { get; } = info;
        public DocumentChunk[] Chunks { get; set; } = [];
        public CancellationTokenSource Cancel { get; } = new();
    }

    private sealed record CacheFile(string Hash, string Settings, string Title, int PageCount, bool HasPages, string? Warning, List<DocumentChunk> Chunks);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly TaskCompletionSource _scanned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<Entry> _queue = Channel.CreateUnbounded<Entry>();
    private readonly SemaphoreSlim _addLock = new(1, 1);
    private readonly EmbeddingService _embedder;
    private readonly RagOptions _options;
    private readonly ILogger<DocumentLibrary> _logger;

    public DocumentLibrary(EmbeddingService embedder, IOptions<RagOptions> options, IHostEnvironment env, ILogger<DocumentLibrary> logger)
    {
        _embedder = embedder;
        _options = options.Value;
        _logger = logger;
        DocsFolder = AppPaths.FindOrCreateFolder(_options.DocsFolder, env.ContentRootPath);
        IndexFolder = AppPaths.FindOrCreateFolder(_options.IndexFolder, env.ContentRootPath);
    }

    public string DocsFolder { get; }
    public string IndexFolder { get; }
    public long MaxUploadBytes => _options.MaxUploadMB * 1024L * 1024L;

    /// <summary>Raised whenever a document is added, removed, or changes status/progress.</summary>
    public event Action? Changed;

    public IReadOnlyList<DocumentInfo> List() =>
        _entries.Values.Select(e => e.Info).OrderBy(d => d.Title, StringComparer.OrdinalIgnoreCase).ToList();

    public DocumentInfo? Get(string id) => _entries.TryGetValue(id, out var e) ? e.Info : null;

    /// <summary>Chunks of ready documents: all of them, or only the given document.</summary>
    public IReadOnlyList<DocumentChunk> GetChunks(string? documentId) =>
        _entries.Values
            .Where(e => e.Info.Status == DocumentStatus.Ready && (documentId is null || e.Info.Id == documentId))
            .SelectMany(e => e.Chunks)
            .ToList();

    public bool HasReadyDocument(string? documentId) =>
        _entries.Values.Any(e => e.Info.Status == DocumentStatus.Ready && (documentId is null || e.Info.Id == documentId));

    /// <summary>Completes when the startup scan of docs/ is done and no document is still indexing.</summary>
    public async Task WaitUntilIdleAsync(CancellationToken ct)
    {
        await _scanned.Task.WaitAsync(ct);
        while (_entries.Values.Any(e => e.Info.Status == DocumentStatus.Indexing))
            await Task.Delay(500, ct);
    }

    // ---------------------------------------------------------------- upload / delete

    /// <summary>Validates and saves an upload to docs/, then queues it for indexing.</summary>
    /// <returns>An error message, or null on success.</returns>
    public async Task<string?> AddAsync(string fileName, Stream content, CancellationToken ct)
    {
        var name = Path.GetFileName(fileName);
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (!DocumentParser.SupportedExtensions.Contains(extension))
            return $"{name}: unsupported file type. Upload PDF, DOCX, TXT or MD files.";

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        if (buffer.Length > MaxUploadBytes)
            return $"{name}: file is larger than {_options.MaxUploadMB} MB.";
        if (buffer.Length == 0)
            return $"{name}: file is empty.";

        var bytes = buffer.ToArray();
        if (!HasExpectedSignature(extension, bytes))
            return $"{name}: the file content doesn't look like a valid {extension.TrimStart('.').ToUpperInvariant()} file.";

        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        await _addLock.WaitAsync(ct);
        try
        {
            if (_entries.TryGetValue(hash, out var existing))
                return $"{name}: this file was already uploaded as \"{existing.Info.FileName}\".";

            var path = UniquePath(SafeFileName(name));
            await File.WriteAllBytesAsync(path, bytes, ct);
            Register(hash, path);
            _logger.LogInformation("Uploaded {File} ({Bytes:N0} bytes)", Path.GetFileName(path), bytes.Length);
            return null;
        }
        finally
        {
            _addLock.Release();
        }
    }

    /// <summary>Removes the document's chunks from the index and deletes its file and cache file.</summary>
    /// <returns>False if the document doesn't exist or is built in.</returns>
    public bool Delete(string id)
    {
        if (_entries.TryGetValue(id, out var existing) && existing.Info.IsBuiltIn)
            return false;
        if (!_entries.TryRemove(id, out var entry))
            return false;

        entry.Cancel.Cancel();
        TryDelete(entry.Info.FilePath);
        TryDelete(CachePath(id));
        TryDelete(PartialPath(id));
        _logger.LogInformation("Deleted document {File}", entry.Info.FileName);
        Changed?.Invoke();
        return true;
    }

    // ---------------------------------------------------------------- background indexing

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Index everything already in docs/ (built-in documents and earlier uploads), smallest
        // first so short documents become ready while a long one is still being indexed.
        await Task.Yield();
        foreach (var path in Directory.EnumerateFiles(DocsFolder).OrderBy(EstimatedPages))
        {
            if (!DocumentParser.SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
                continue;
            try
            {
                var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, stoppingToken))).ToLowerInvariant();
                if (_entries.TryGetValue(hash, out var existing))
                    _logger.LogWarning("Skipping {File}: same content as {Other}", Path.GetFileName(path), existing.Info.FileName);
                else
                    Register(hash, path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Could not read {File}", path);
            }
        }
        foreach (var name in _options.BuiltInDocuments.Where(n => !File.Exists(Path.Combine(DocsFolder, n))))
            _logger.LogWarning("Built-in document {File} is missing from {Folder}; restore it to answer questions about it.", name, DocsFolder);

        _scanned.TrySetResult();
        _logger.LogInformation("Found {Count} document(s) in {Folder}", _entries.Count, DocsFolder);

        await foreach (var entry in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (!_entries.ContainsKey(entry.Info.Id))
                continue; // deleted while waiting
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, entry.Cancel.Token);
            await IndexAsync(entry, linked.Token);
        }
    }

    private void Register(string hash, string path)
    {
        var fileName = Path.GetFileName(path);
        var builtIn = _options.BuiltInDocuments.Contains(fileName, StringComparer.OrdinalIgnoreCase);
        var entry = new Entry(new DocumentInfo(hash, fileName, path) { IsBuiltIn = builtIn });
        if (!_entries.TryAdd(hash, entry))
            return;
        _queue.Writer.TryWrite(entry);
        Changed?.Invoke();
    }

    private async Task IndexAsync(Entry entry, CancellationToken ct)
    {
        var info = entry.Info;
        try
        {
            if (!_embedder.IsReady)
                throw new InvalidOperationException(_embedder.LoadError);

            var cache = await TryLoadCacheAsync(info.Id, ct);
            if (cache is null)
            {
                _logger.LogInformation("Indexing {File} (no cache yet; this can take several minutes on a CPU)...", info.FileName);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                cache = await BuildIndexAsync(entry, ct);
                _logger.LogInformation("Indexed {File}: {Chunks} chunks in {Seconds:F0}s", info.FileName, cache.Chunks.Count, sw.Elapsed.TotalSeconds);
                await SaveCacheAsync(cache, ct);
            }
            else
            {
                _logger.LogInformation("Loaded {File} from cache ({Chunks} chunks)", info.FileName, cache.Chunks.Count);
            }

            foreach (var chunk in cache.Chunks)
                KeywordIndex.Prepare(chunk);

            entry.Chunks = cache.Chunks.ToArray();
            info.Title = cache.Title;
            info.PageCount = cache.PageCount;
            info.HasPages = cache.HasPages;
            info.Warning = cache.Warning;
            info.ChunkCount = cache.Chunks.Count;

            if (cache.Chunks.Count == 0)
            {
                info.Status = DocumentStatus.Failed;
                info.Error = cache.Warning ?? "No text could be extracted from this file.";
            }
            else
            {
                info.Status = DocumentStatus.Ready;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // deleted or shutting down
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Indexing failed for {File}", info.FileName);
            info.Status = DocumentStatus.Failed;
            info.Error = ex.Message;
        }
        Changed?.Invoke();
    }

    private async Task<CacheFile> BuildIndexAsync(Entry entry, CancellationToken ct)
    {
        var info = entry.Info;
        var parsed = await Task.Run(() => DocumentParser.Parse(info.FilePath), ct);
        var title = parsed.Title ?? Path.GetFileNameWithoutExtension(info.FileName);
        info.Title = title;
        info.PageCount = parsed.LocationCount;
        info.HasPages = parsed.HasPages;
        info.Warning = parsed.Warnings.Count > 0 ? string.Join(" ", parsed.Warnings) : null;

        var pieces = TextChunker.Chunk(parsed, _embedder.CountTokens, _options.ChunkTokens, _options.ChunkOverlapTokens);
        info.ChunkCount = pieces.Count;
        Changed?.Invoke();

        // Resume an interrupted run: reuse vectors from the checkpoint while the chunks still match.
        var partial = await TryLoadCacheFileAsync(PartialPath(info.Id), info.Id, ct);
        var resumable = partial?.Chunks ?? [];
        if (resumable.Count > 0)
            _logger.LogInformation("Resuming {File} from checkpoint ({Done}/{Total} chunks)", info.FileName, Math.Min(resumable.Count, pieces.Count), pieces.Count);

        var chunks = new List<DocumentChunk>(pieces.Count);
        var lastReport = DateTime.UtcNow;
        var sinceCheckpoint = 0;
        foreach (var piece in pieces)
        {
            var section = piece.Section.Length > 0 ? piece.Section : title;
            var i = chunks.Count;
            if (i < resumable.Count && resumable[i].Text == piece.Text && resumable[i].Section == section)
            {
                chunks.Add(resumable[i]);
                continue;
            }

            var chunk = new DocumentChunk
            {
                DocumentId = info.Id,
                Document = title,
                Section = section,
                PageStart = parsed.Label(piece.LocationStart),
                PageEnd = parsed.Label(piece.LocationEnd),
                HasPages = parsed.HasPages,
                Text = piece.Text,
            };
            chunk.Embedding = await _embedder.EmbedDocumentAsync($"{section}\n{piece.Text}", ct);
            chunks.Add(chunk);

            info.Progress = chunks.Count / (double)pieces.Count;
            if (DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(1))
            {
                lastReport = DateTime.UtcNow;
                Changed?.Invoke();
            }

            // Checkpoint so a restart doesn't redo minutes of embedding.
            if (++sinceCheckpoint >= CheckpointEvery && chunks.Count < pieces.Count)
            {
                sinceCheckpoint = 0;
                await SaveFileAsync(PartialPath(info.Id), Result(chunks), ct);
            }
        }

        return Result(chunks);

        CacheFile Result(List<DocumentChunk> list) =>
            new(info.Id, SettingsFingerprint, title, parsed.LocationCount, parsed.HasPages, info.Warning, list);
    }

    // ---------------------------------------------------------------- cache files

    /// <summary>Everything that affects the chunks or vectors. A cache file is reused only if this matches.</summary>
    private string SettingsFingerprint =>
        $"v{IndexVersion}|{_embedder.ModelFingerprint}|{_options.DocumentPrefix}|chunk={_options.ChunkTokens}/{_options.ChunkOverlapTokens}";

    private string CachePath(string hash) => Path.Combine(IndexFolder, hash + ".json");

    /// <summary>Checkpoint of a document that is still being indexed.</summary>
    private string PartialPath(string hash) => Path.Combine(IndexFolder, hash + ".partial.json");

    private Task<CacheFile?> TryLoadCacheAsync(string hash, CancellationToken ct) =>
        TryLoadCacheFileAsync(CachePath(hash), hash, ct);

    private async Task<CacheFile?> TryLoadCacheFileAsync(string path, string hash, CancellationToken ct)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            await using var stream = File.OpenRead(path);
            var cache = await JsonSerializer.DeserializeAsync<CacheFile>(stream, JsonOptions, ct);
            if (cache is not null && cache.Hash == hash && cache.Settings == SettingsFingerprint)
                return cache;
            _logger.LogInformation("{File} is out of date; re-indexing", Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Ignoring unreadable cache file {Path}", path);
        }
        return null;
    }

    private async Task SaveCacheAsync(CacheFile cache, CancellationToken ct)
    {
        await SaveFileAsync(CachePath(cache.Hash), cache, ct);
        TryDelete(PartialPath(cache.Hash));
    }

    private static async Task SaveFileAsync(string path, CacheFile cache, CancellationToken ct)
    {
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, cache, JsonOptions, ct);
        File.Move(temp, path, overwrite: true);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Rough size for ordering the startup queue: PDF page count, or ~3 KB of text per page.</summary>
    private static int EstimatedPages(string path)
    {
        try
        {
            if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
                return pdf.NumberOfPages;
            }
        }
        catch
        {
            // unreadable PDF: indexing will report the error
        }
        return (int)Math.Min(int.MaxValue, new FileInfo(path).Length / 3000);
    }

    private static bool HasExpectedSignature(string extension, byte[] bytes) => extension switch
    {
        ".pdf" => bytes.Length > 4 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F',
        ".docx" => bytes.Length > 2 && bytes[0] == 'P' && bytes[1] == 'K',
        _ => !bytes.Take(4096).Contains((byte)0), // text files: no NUL bytes
    };

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        return safe.Length == 0 ? "document" : safe;
    }

    private string UniquePath(string name)
    {
        var path = Path.Combine(DocsFolder, name);
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(DocsFolder, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
        return path;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete {Path}", path);
        }
    }
}
