namespace ChatBot.Services;

/// <summary>
/// Resolves relative paths like "models/x.gguf" or "docs". The app runs from ChatBot/ but these
/// folders live at the repo root, so relative paths are looked up from the content root upwards.
/// </summary>
public static class AppPaths
{
    public static string? FindFile(string path, string contentRoot)
    {
        if (Path.IsPathRooted(path))
            return File.Exists(path) ? path : null;

        for (var dir = new DirectoryInfo(contentRoot); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, path);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>Finds an existing folder, or creates it at the repo root (the folder holding the .sln).</summary>
    public static string FindOrCreateFolder(string path, string contentRoot)
    {
        if (Path.IsPathRooted(path))
            return Directory.CreateDirectory(path).FullName;

        DirectoryInfo? repoRoot = null;
        for (var dir = new DirectoryInfo(contentRoot); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, path);
            if (Directory.Exists(candidate))
                return candidate;
            if (repoRoot is null && dir.EnumerateFiles("*.sln").Any())
                repoRoot = dir;
        }
        return Directory.CreateDirectory(Path.Combine((repoRoot ?? new DirectoryInfo(contentRoot)).FullName, path)).FullName;
    }
}
