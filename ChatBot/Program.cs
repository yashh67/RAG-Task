using ChatBot.Components;
using ChatBot.Services;
using ChatBot.Services.Rag;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection("Llm"));
builder.Services.Configure<RagOptions>(builder.Configuration.GetSection("Rag"));
builder.Services.AddSingleton<LlmService>();
builder.Services.AddSingleton<PromptBuilder>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<ChatService>();

// Documents: embedding model, shared library (indexed in the background), hybrid search.
builder.Services.AddSingleton<EmbeddingService>();
builder.Services.AddSingleton<DocumentLibrary>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DocumentLibrary>());
builder.Services.AddSingleton<Retriever>();

var app = builder.Build();

// Send llama.cpp's native logging through ASP.NET Core logging (filtered in appsettings).
// Must happen before the native library loads.
LLama.Native.NativeLibraryConfig.All.WithLogCallback(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LLama.Native"));

// "--index-only": index everything in docs/ (writing data/index/*.json), print a summary and exit.
// Lets you build the cache ahead of time so documents are ready the moment the app starts.
if (args.Contains("--index-only"))
{
    var library = app.Services.GetRequiredService<DocumentLibrary>();
    await library.StartAsync(CancellationToken.None);
    await library.WaitUntilIdleAsync(CancellationToken.None);
    foreach (var doc in library.List())
        Console.WriteLine($"{doc.Status,-8} {doc.FileName}: {doc.PageCount} {(doc.HasPages ? "pages" : "paragraphs")}, {doc.ChunkCount} chunks{(doc.Warning is null ? "" : " ⚠ " + doc.Warning)}{(doc.Error is null ? "" : " ✗ " + doc.Error)}");
    await library.StopAsync(CancellationToken.None);
    return;
}

// Load the models once, at startup (logs a clear error instead of crashing if a file is missing).
app.Services.GetRequiredService<LlmService>();
app.Services.GetRequiredService<EmbeddingService>();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

// Give every browser an anonymous user ID. Sessions are keyed by it.
app.Use(async (context, next) =>
{
    if (!context.Request.Cookies.TryGetValue(UserIdCookie.Name, out var userId) || !Guid.TryParse(userId, out _))
    {
        userId = Guid.NewGuid().ToString();
        context.Response.Cookies.Append(UserIdCookie.Name, userId, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            Expires = DateTimeOffset.UtcNow.AddYears(1),
        });
    }
    context.Items[UserIdCookie.Name] = userId;
    await next();
});

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public static class UserIdCookie
{
    public const string Name = "chatbot_uid";
}
