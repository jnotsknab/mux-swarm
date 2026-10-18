using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace MuxSwarm.Engine.Voice;

/// <summary>
/// Server-side speech-to-text for the web UI and desktop shells: <c>POST /api/voice/transcribe</c>.
/// Runs the same local whisper.cpp assets that back the TUI <c>/voice</c> command
/// (<see cref="WhisperAssets"/>) on an uploaded audio clip and returns the transcript.
/// Assets are never downloaded inside a request: when they are missing the endpoint answers
/// 503 and a later <c>/voice</c> (which provisions them in the background) makes it work.
/// </summary>
internal static class VoiceTranscribeEndpoint
{
    /// <summary>Maximum accepted request body, in bytes (25 MB).</summary>
    internal const long MaxBytes = 25L * 1024 * 1024;

    /// <summary>Maximum accepted audio duration.</summary>
    internal static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(5);

    /// <summary>Wall-clock limit for one whisper-cli (or ffmpeg) run before that process is killed.</summary>
    internal static TimeSpan ProcessTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Path of the whisper-cli executable. Tests swap in a mock.</summary>
    internal static Func<string> CliPath = () => WhisperAssets.CliPath;

    /// <summary>Directory searched for <c>*.bin</c> models. Tests swap in a temp dir.</summary>
    internal static Func<string> ModelsDir = () => WhisperAssets.ModelsDir;

    /// <summary>Path of ffmpeg used to convert non-WAV uploads, or null when it is not on PATH.</summary>
    internal static Func<string?> FfmpegPath = () => FindOnPath(PlatformContext.IsWindows ? "ffmpeg.exe" : "ffmpeg");

    /// <summary>One transcription at a time; a concurrent request gets 429.</summary>
    internal static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly string[] WavTypes = { "audio/wav", "audio/x-wav", "audio/wave" };
    private static readonly string[] OtherTypes = { "audio/webm", "audio/ogg", "audio/mpeg" };

    private static readonly Regex AnnotationLine = new(@"^(\[[^\]]*\]|\([^\)]*\))$", RegexOptions.Compiled);

    /// <summary>
    /// <c>POST /api/voice/transcribe</c>: transcribe one audio clip with local whisper.cpp.
    /// Body is the raw audio with <c>Content-Type</c> <c>audio/wav</c>, <c>audio/webm</c>,
    /// <c>audio/ogg</c> or <c>audio/mpeg</c> (non-WAV needs ffmpeg on PATH). Limits: 25 MB, 5 minutes.
    /// Returns 200 <c>{ text, ms, model }</c>, where <c>text</c> has whisper non-speech annotations
    /// removed and <c>model</c> is the model file name without extension. Silent clips (16-bit PCM
    /// below about -46 dBFS throughout) return <c>""</c> without running whisper.
    /// Errors (all <c>{ error }</c>): 413 too large/long, 415 unsupported type or undecodable audio,
    /// 429 another transcription is running, 503 whisper binary or model missing, 504 whisper timed out,
    /// 500 whisper failed. Protected by the same <c>serve.auth</c> bearer gate as every <c>/api</c> route.
    /// </summary>
    /// <param name="context">The HTTP request context.</param>
    public static async Task Handle(HttpContext context)
    {
        var mediaType = MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var ct)
            ? ct.MediaType?.ToLowerInvariant() ?? "" : "";
        bool isWav = WavTypes.Contains(mediaType);
        if (!isWav && !OtherTypes.Contains(mediaType))
        {
            await Json(context, 415, new { error = "Content-Type must be audio/wav, audio/webm, audio/ogg or audio/mpeg" });
            return;
        }
        if (context.Request.ContentLength > MaxBytes)
        {
            await Json(context, 413, new { error = "audio exceeds 25 MB" });
            return;
        }

        var cli = CliPath();
        var model = ResolveModel(ModelsDir());
        if (!File.Exists(cli) || model is null)
        {
            await Json(context, 503, new { error = "whisper assets not installed; run /voice in the TUI once to download them" });
            return;
        }

        string? ffmpeg = null;
        if (!isWav && (ffmpeg = FfmpegPath()) is null)
        {
            await Json(context, 415, new { error = $"{mediaType} needs ffmpeg on PATH to convert; send audio/wav instead" });
            return;
        }

        if (!await Gate.WaitAsync(0))
        {
            await Json(context, 429, new { error = "busy" });
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), $"mux-transcribe-{Guid.NewGuid():N}");
        try
        {
            var sw = Stopwatch.StartNew();
            Directory.CreateDirectory(dir);
            var input = Path.Combine(dir, isWav ? "in.wav" : "in.audio");
            long size;
            await using (var fs = File.Create(input))
                size = await CopyCappedAsync(context.Request.Body, fs, MaxBytes, context.RequestAborted);
            if (size > MaxBytes)
            {
                await Json(context, 413, new { error = "audio exceeds 25 MB" });
                return;
            }

            var wav = input;
            if (!isWav)
            {
                wav = Path.Combine(dir, "in.wav");
                var conv = await RunAsync(ffmpeg!,
                    $"-nostdin -hide_banner -loglevel error -y -i \"{input}\" -ar 16000 -ac 1 -c:a pcm_s16le -t {(int)MaxDuration.TotalSeconds + 1} \"{wav}\"", dir);
                if (conv.TimedOut || conv.ExitCode != 0 || !File.Exists(wav))
                {
                    await Json(context, 415, new { error = "could not decode audio: " + Tail(conv.Stderr) });
                    return;
                }
            }

            var info = ReadWav(wav);
            if (info is null)
            {
                await Json(context, 415, new { error = "not a valid PCM WAV file" });
                return;
            }
            if (info.Duration > MaxDuration)
            {
                await Json(context, 413, new { error = "audio exceeds 5 minutes" });
                return;
            }

            var modelName = Path.GetFileNameWithoutExtension(model);
            if (IsSilent(wav, info))
            {
                await Json(context, 200, new { text = "", ms = sw.ElapsedMilliseconds, model = modelName });
                return;
            }
            var lang = modelName.Contains(".en", StringComparison.OrdinalIgnoreCase) ? "en" : "auto";
            var run = await RunAsync(cli, $"-m \"{model}\" -f \"{wav}\" -nt -np -nth 0.6 -l {lang} -t 4", Path.GetDirectoryName(cli)!);
            if (run.TimedOut)
            {
                await Json(context, 504, new { error = "whisper-cli timed out" });
                return;
            }
            if (run.ExitCode != 0)
            {
                await Json(context, 500, new { error = $"whisper-cli exited {run.ExitCode}: {Tail(run.Stderr)}" });
                return;
            }

            await Json(context, 200, new { text = FilterNonSpeech(run.Stdout), ms = sw.ElapsedMilliseconds, model = modelName });
        }
        finally
        {
            Gate.Release();
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Drop whisper non-speech annotations that occupy a whole line (<c>[BLANK_AUDIO]</c>,
    /// <c>(air whooshing)</c>, <c>[MUSIC]</c>), then join the remaining lines and collapse whitespace.
    /// </summary>
    internal static string FilterNonSpeech(string raw)
    {
        var kept = (raw ?? "").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !AnnotationLine.IsMatch(l));
        return Regex.Replace(string.Join(' ', kept), @"\s+", " ").Trim();
    }

    /// <summary>The pinned model when present, else the first <c>*.bin</c> (ordinal order), else null.</summary>
    internal static string? ResolveModel(string modelsDir)
    {
        if (!Directory.Exists(modelsDir)) return null;
        var pinned = Path.Combine(modelsDir, WhisperAssets.ModelFileName);
        if (File.Exists(pinned)) return pinned;
        return Directory.GetFiles(modelsDir, "*.bin").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>Parsed RIFF/WAVE header facts needed for the duration cap and the silence gate.</summary>
    internal sealed record WavInfo(TimeSpan Duration, int BitsPerSample, long DataOffset, long DataLength);

    /// <summary>Parse a RIFF/WAVE file's fmt and data chunks, or null when it is not a usable WAV.</summary>
    internal static WavInfo? ReadWav(string path)
    {
        try
        {
            using var r = new BinaryReader(File.OpenRead(path));
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "RIFF") return null;
            r.ReadInt32();
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "WAVE") return null;
            int byteRate = 0, bits = 0;
            while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
            {
                var id = Encoding.ASCII.GetString(r.ReadBytes(4));
                long len = r.ReadUInt32();
                if (id == "fmt ")
                {
                    r.ReadBytes(8);
                    byteRate = r.ReadInt32();
                    r.ReadInt16();
                    bits = r.ReadInt16();
                    r.BaseStream.Seek(len - 16 + (len & 1), SeekOrigin.Current);
                }
                else if (id == "data")
                {
                    if (byteRate <= 0) return null;
                    // Streamed WAVs may carry a placeholder size; trust the bytes actually present.
                    long data = Math.Min(len, r.BaseStream.Length - r.BaseStream.Position);
                    return new WavInfo(TimeSpan.FromSeconds((double)data / byteRate), bits, r.BaseStream.Position, data);
                }
                else r.BaseStream.Seek(len + (len & 1), SeekOrigin.Current);
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Frame RMS floor below which a clip counts as silence (about -46 dBFS). whisper hallucinates
    /// words such as "you" on digital silence even with <c>--no-speech-thold</c>, so silent clips
    /// never reach it.
    /// </summary>
    internal const double SilenceRms = 0.005;

    /// <summary>
    /// True when no 30 ms frame of a 16-bit PCM WAV rises above <see cref="SilenceRms"/>. Other
    /// sample formats are never treated as silent (whisper decides).
    /// </summary>
    internal static bool IsSilent(string path, WavInfo info)
    {
        if (info.BitsPerSample != 16) return false;
        using var fs = File.OpenRead(path);
        fs.Seek(info.DataOffset, SeekOrigin.Begin);
        var frame = new byte[960]; // 30 ms at 16 kHz mono 16-bit
        long left = info.DataLength;
        while (left > 1)
        {
            int n = fs.Read(frame, 0, (int)Math.Min(frame.Length, left));
            if (n <= 0) break;
            left -= n;
            var chunk = n == frame.Length ? frame : frame[..n];
            if (VoiceSession.FrameRms(chunk) > SilenceRms) return false;
        }
        return true;
    }

    private sealed record RunResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);

    private static async Task<RunResult> RunAsync(string file, string args, string workDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workDir,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {file}");
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(ProcessTimeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return new RunResult(-1, "", "", TimedOut: true);
        }
        return new RunResult(p.ExitCode, await stdout, await stderr, TimedOut: false);
    }

    private static async Task<long> CopyCappedAsync(Stream src, Stream dst, long cap, CancellationToken ct)
    {
        var buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            total += n;
            if (total > cap) return total;
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
        }
        return total;
    }

    private static string Tail(string s)
    {
        s = (s ?? "").Trim();
        return s.Length <= 300 ? s : s[^300..];
    }

    private static string? FindOnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir.Trim(), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }

    private static Task Json(HttpContext context, int status, object data)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(JsonSerializer.Serialize(data));
    }
}
