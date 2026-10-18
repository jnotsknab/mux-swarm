using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using MuxSwarm.Engine.NativeTools;

namespace MuxSwarm.Engine;

/// <summary>
/// File endpoints for the web UI's file chips, inline images and workspace panel. A path (absolute, or
/// relative to the sandbox) is served when it lies under the sandbox, the sessions folder, or any
/// <c>filesystem.allowedPaths</c> root: the same roots the agent's own file tools use. Covered by the
/// existing serve.auth middleware like every other /api route.
///
///   GET  /api/file?path=&amp;as=view|download   file bytes (Range supported); as=download adds attachment
///   GET  /api/file/meta?path=                  { path, name, size, mtime, mime, kind }
///   POST /api/file/reveal { path }             show the file in the OS file manager (serve host)
/// </summary>
public static partial class ServeMode
{
    private static readonly FileExtensionContentTypeProvider _mime = new();

    /// <summary>Launches the OS file manager; replaced in tests so nothing opens.</summary>
    internal static Func<ProcessStartInfo, bool> RevealLauncher = psi =>
    {
        try { using var p = Process.Start(psi); return p != null; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    };

    /// <summary>Roots a web UI file request may resolve under (sandbox, sessions, allowedPaths).</summary>
    internal static IReadOnlyList<string> FileRoots()
    {
        var roots = new List<string>();
        if (!string.IsNullOrEmpty(_sandboxRoot)) roots.Add(_sandboxRoot);
        if (!string.IsNullOrEmpty(_sessionsRoot)) roots.Add(_sessionsRoot);
        roots.AddRange((App.Config?.Filesystem?.AllowedPaths ?? []).Where(p => !string.IsNullOrWhiteSpace(p)));
        return roots;
    }

    /// <summary>
    /// Resolve <paramref name="path"/> to an existing file under one of <paramref name="roots"/>. Relative
    /// paths are taken against the first root (the sandbox). Null when missing or outside every root.
    /// </summary>
    internal static string? ResolveServableFile(string? path, IReadOnlyList<string> roots)
    {
        if (string.IsNullOrWhiteSpace(path) || roots.Count == 0) return null;
        try
        {
            var p = path.Trim();
            if (p.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) p = new Uri(p).LocalPath;
            var full = Path.IsPathRooted(p) ? Path.GetFullPath(p) : Path.GetFullPath(Path.Combine(roots[0], p));
            return File.Exists(full) && NativeToolSecurity.IsUnderAllowed(full, roots) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or UriFormatException or IOException)
        {
            return null;
        }
    }

    /// <summary>Coarse renderer hint for the web UI from the file extension.</summary>
    internal static string FileKind(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".ico" or ".avif" or ".svg" => "image",
            ".mp4" or ".webm" or ".mov" or ".m4v" or ".ogv" => "video",
            ".mp3" or ".wav" or ".ogg" or ".flac" or ".m4a" or ".aac" or ".opus" => "audio",
            ".pdf" => "pdf",
            ".md" or ".markdown" => "markdown",
            ".html" or ".htm" => "html",
            ".json" or ".jsonl" => "json",
            ".csv" or ".tsv" => "table",
            _ => _mime.TryGetContentType(path, out var ct) && ct.StartsWith("text/", StringComparison.Ordinal) ? "text"
                : TextExtensions.Contains(ext) ? "text" : "binary",
        };
    }

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".py", ".ts", ".tsx", ".jsx", ".rs", ".go", ".zig", ".java", ".kt", ".c", ".h", ".cpp", ".hpp", ".rb",
        ".php", ".lua", ".sh", ".ps1", ".bat", ".toml", ".yaml", ".yml", ".ini", ".cfg", ".conf", ".log", ".sql",
        ".xml", ".csproj", ".sln", ".slnx", ".props", ".diff", ".patch", ".ipynb", ".env", ".txt",
    };

    private static string MimeOf(string path) =>
        _mime.TryGetContentType(path, out var ct) ? ct
        : TextExtensions.Contains(Path.GetExtension(path)) ? "text/plain; charset=utf-8" : "application/octet-stream";

    private static async Task HandleFile(HttpContext context)
    {
        var full = ResolveServableFile(context.Request.Query["path"].FirstOrDefault(), FileRoots());
        if (full == null) { await WriteJson(context, 404, new { error = "File not found" }); return; }
        var download = string.Equals(context.Request.Query["as"].FirstOrDefault(), "download", StringComparison.OrdinalIgnoreCase);
        await Results.File(full, MimeOf(full), download ? Path.GetFileName(full) : null, enableRangeProcessing: true)
            .ExecuteAsync(context);
    }

    private static async Task HandleFileMeta(HttpContext context)
    {
        var full = ResolveServableFile(context.Request.Query["path"].FirstOrDefault(), FileRoots());
        if (full == null) { await WriteJson(context, 404, new { error = "File not found" }); return; }
        var fi = new FileInfo(full);
        await WriteJson(context, 200, new
        {
            path = full,
            name = fi.Name,
            size = fi.Length,
            mtime = fi.LastWriteTimeUtc.ToString("o"),
            mime = MimeOf(full),
            kind = FileKind(full),
        });
    }

    private static async Task HandleFileReveal(HttpContext context)
    {
        string? path = null;
        try
        {
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body);
            if (doc.RootElement.TryGetProperty("path", out var p)) path = p.GetString();
        }
        catch (System.Text.Json.JsonException) { }
        var full = ResolveServableFile(path, FileRoots());
        if (full == null) { await WriteJson(context, 404, new { error = "File not found" }); return; }
        var psi = RevealCommand(full);
        if (psi == null || !RevealLauncher(psi)) { await WriteJson(context, 501, new { error = "No file manager available on the serve host" }); return; }
        await WriteJson(context, 200, new { ok = true, path = full });
    }

    /// <summary>The OS command that shows <paramref name="full"/> in a file manager (argv, never a shell string).</summary>
    internal static ProcessStartInfo? RevealCommand(string full)
    {
        ProcessStartInfo psi;
        if (PlatformContext.IsWindows) { psi = new ProcessStartInfo("explorer.exe"); psi.ArgumentList.Add("/select," + full); }
        else if (PlatformContext.IsMac) { psi = new ProcessStartInfo("open"); psi.ArgumentList.Add("-R"); psi.ArgumentList.Add(full); }
        else if (PlatformContext.IsLinux) { psi = new ProcessStartInfo("xdg-open"); psi.ArgumentList.Add(Path.GetDirectoryName(full) ?? full); }
        else return null;
        psi.UseShellExecute = false;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;
        return psi;
    }
}
