using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MuxSwarm.Engine.NativeTools;

namespace MuxSwarm.Engine.Tui;

/// <summary>Bounded clipboard ingress and create-new capture persistence; never reads console input.</summary>
internal static class ClipboardCapture
{
    internal const int MaxBytes = 16 * 1024 * 1024;
    internal sealed record Payload(string? Text = null, byte[]? Image = null, string? Error = null);
    internal static bool IsRemote => new[] { "SSH_CONNECTION", "SSH_TTY", "SSH_CLIENT" }
        .Any(n => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(n)));

    internal static async Task<Payload> ReadAsync(CancellationToken token)
    {
        if (IsRemote) return new(Error: "Remote clipboard is not your local clipboard. Use an enhanced-paste terminal or upload an image and paste its path.");
        try
        {
            bool wsl = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME"));
            if (OperatingSystem.IsWindows())
            {
                var png = ReadWindowsPng();
                if (png is not null)
                {
                    try { _ = Extension(png); return new(Image: png); }
                    catch (IOException) { /* malformed native PNG: try OS bitmap conversion */ }
                }
            }
            if (OperatingSystem.IsWindows() || wsl)
            {
                // Fixed script, no interpolated paths/text. PowerShell provides the DIB/DIBV5 -> PNG fallback.
                const string script = "Add-Type -AssemblyName System.Windows.Forms; Add-Type -AssemblyName System.Drawing; " +
                    "$i=[System.Windows.Forms.Clipboard]::GetImage(); if($null -ne $i){try{$m=New-Object IO.MemoryStream; " +
                    "$i.Save($m,[System.Drawing.Imaging.ImageFormat]::Png); [Console]::Out.Write('I:'+ [Convert]::ToBase64String($m.ToArray()))}finally{$i.Dispose(); if($m){$m.Dispose()}}} " +
                    "else{[Console]::Out.Write('T:'+ [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([System.Windows.Forms.Clipboard]::GetText())))}";
                var result = await RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Sta", "-Command", script], token);
                if (result is not null)
                {
                    string wire = Encoding.UTF8.GetString(result).Trim();
                    if (wire.StartsWith("I:")) return new(Image: Convert.FromBase64String(wire[2..]));
                    if (wire.StartsWith("T:")) return new(Text: Encoding.UTF8.GetString(Convert.FromBase64String(wire[2..])));
                }
                if (OperatingSystem.IsWindows()) return new(Error: "Clipboard read failed. Retry Ctrl+V / Alt+V or paste an image-file path.");
            }
            if (OperatingSystem.IsMacOS())
            {
                const string jxa = "ObjC.import('AppKit'); ObjC.import('Foundation'); var p=$.NSPasteboard.generalPasteboard; " +
                    "var d=p.dataForType($.NSPasteboardTypePNG); if(!d){var t=p.dataForType($.NSPasteboardTypeTIFF); " +
                    "if(t){var b=$.NSBitmapImageRep.imageRepWithData(t);d=b.representationUsingTypeProperties($.NSBitmapImageFileTypePNG,$())}} " +
                    "d ? ObjC.unwrap(d.base64EncodedStringWithOptions(0)) : ''";
                var result = await RunAsync("osascript", ["-l", "JavaScript", "-e", jxa], token);
                if (result is { Length: > 0 })
                {
                    var b64 = Encoding.UTF8.GetString(result).Trim();
                    if (b64.Length > 0) return new(Image: Convert.FromBase64String(b64));
                }
                var text = await RunAsync("pbpaste", [], token);
                return text is null ? new(Error: "macOS clipboard unavailable.") : new(Text: Encoding.UTF8.GetString(text));
            }
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            {
                var image = await RunAsync("wl-paste", ["--type", "image/png"], token);
                if (image is { Length: > 0 }) return new(Image: image);
                var text = await RunAsync("wl-paste", ["--type", "text", "--no-newline"], token);
                if (text is not null) return new(Text: Encoding.UTF8.GetString(text));
            }
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            {
                var image = await RunAsync("xclip", ["-selection", "clipboard", "-t", "image/png", "-o"], token);
                if (image is { Length: > 0 }) return new(Image: image);
                var text = await RunAsync("xclip", ["-selection", "clipboard", "-o"], token);
                if (text is not null) return new(Text: Encoding.UTF8.GetString(text));
            }
            return new(Error: "Clipboard unavailable. Linux needs wl-paste (Wayland) or xclip (X11); otherwise paste a readable file path.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(Error: "Clipboard read failed: " + ex.Message); }
    }

    /// <summary>Only an explicit single allowed image path is read; ordinary pasted prose remains text.</summary>
    internal static async Task<byte[]?> ReadImagePathAsync(string text, IReadOnlyList<string> allowed, CancellationToken token)
    {
        string path = text.Trim().Trim('"', '\'');
        if (path.Contains('\n') || path.Contains('\r')) return null;
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile) path = uri.LocalPath;
        if (!Path.IsPathFullyQualified(path) || !new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" }.Contains(Path.GetExtension(path).ToLowerInvariant())) return null;
        if (!NativeToolSecurity.IsUnderAllowed(path, allowed)) return null;
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        if (stream.Length > MaxBytes) throw new IOException("Image exceeds the 16 MiB capture limit.");
        using var result = new MemoryStream();
        byte[] buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (result.Length + count > MaxBytes) throw new IOException("Image exceeds the 16 MiB capture limit.");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    internal static string Extension(ReadOnlySpan<byte> image)
    {
        if (image.Length is 0 or > MaxBytes) throw new IOException("Image must be between 1 byte and 16 MiB.");
        if (image.Length >= 33 && image[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && image.Slice(12, 4).SequenceEqual("IHDR"u8)) return ".png";
        if (image.Length >= 4 && image[0] == 255 && image[1] == 216 && image[2] == 255) return ".jpg";
        if (image.Length >= 13 && (image[..6].SequenceEqual("GIF89a"u8) || image[..6].SequenceEqual("GIF87a"u8))) return ".gif";
        if (image.Length >= 16 && image[..4].SequenceEqual("RIFF"u8) && image.Slice(8, 4).SequenceEqual("WEBP"u8)) return ".webp";
        if (image.Length >= 54 && image[..2].SequenceEqual("BM"u8)) return ".bmp";
        throw new IOException("Clipboard payload is not a supported image.");
    }

    /// <summary>Validate format and containment, then atomically publish a unique capture without overwriting.</summary>
    internal static string Save(byte[] bytes, string sandbox, IReadOnlyList<string> allowed)
    {
        string extension = Extension(bytes);
        if (string.IsNullOrWhiteSpace(sandbox)) throw new IOException("Configure a sandbox before pasting screenshots.");
        string root = Path.GetFullPath(sandbox);
        if (!NativeToolSecurity.IsUnderAllowed(root, allowed)) throw new UnauthorizedAccessException("Sandbox is outside allowed paths.");
        string dir = Path.Combine(root, "captures");
        for (var ancestor = new DirectoryInfo(dir); ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Capture paths must not traverse symbolic links or junctions.");
        if (Directory.Exists(dir) && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The captures directory must not be a symbolic link or junction.");
        Directory.CreateDirectory(dir);
        if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) throw new IOException("Unsafe captures directory.");
        string file = Path.Combine(dir, $"capture-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}{extension}");
        string temp = file + ".partial";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, file, overwrite: false);
            return file;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static async Task<byte[]?> RunAsync(string file, string[] args, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var process = new Process { StartInfo = new ProcessStartInfo(file) {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        try
        {
            if (!process.Start()) return null;
            process.StandardInput.Close();
            var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            using var output = new MemoryStream();
            var buffer = new byte[8192]; int read;
            while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (output.Length + read > MaxBytes * 2L) throw new IOException("Clipboard output too large.");
                output.Write(buffer, 0, read);
            }
            await process.WaitForExitAsync(deadline.Token); await error;
            return process.ExitCode == 0 ? output.ToArray() : null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
        finally { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } }
    }

    private static byte[]? ReadWindowsPng()
    {
        if (!OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var handle = GetClipboardData(RegisterClipboardFormat("PNG"));
            if (handle == IntPtr.Zero) return null;
            ulong size = GlobalSize(handle).ToUInt64();
            if (size == 0 || size > MaxBytes) return null;
            var ptr = GlobalLock(handle); if (ptr == IntPtr.Zero) return null;
            try { var bytes = new byte[(int)size]; Marshal.Copy(ptr, bytes, 0, bytes.Length); return bytes; }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }

    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
}
