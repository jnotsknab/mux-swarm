using System.Diagnostics;
using System.Text;

namespace MuxSwarm.Utils.Voice;

/// <summary>Voice pipeline state, driven by <see cref="VoiceSession"/> and rendered by the TUI
/// compose-field indicator (the prompt caret becomes a state dot while voice is active).</summary>
internal enum VoiceState
{
    Off,
    /// <summary>Assets downloading / model warming - dim slow blink.</summary>
    Warming,
    /// <summary>Mic open, waiting for speech - steady dot.</summary>
    Listening,
    /// <summary>Speech energy detected right now - fast accent pulse.</summary>
    Hearing,
    /// <summary>A chunk is being transcribed - spinner.</summary>
    Transcribing,
    /// <summary>Unrecoverable error (no mic / assets failed) - red cross until /voice off.</summary>
    Error,
}

/// <summary>
/// The /voice engine: mic -> energy-VAD segmentation -> whisper.cpp transcription -> compose
/// buffer. One app-wide session (voice is a global input mode, like the delimiter). All work
/// runs on a dedicated background thread; transcripts are handed to the TUI via
/// MuxConsole.InjectComposeText, which appends into the live line editor exactly as if typed.
///
/// Modes: manual (default) - transcripts append to the buffer; the user edits and presses Enter.
///        Saying exactly "send" (nothing else in the chunk) submits the buffer.
///        auto (/voice auto) - after ~1.8s of post-speech silence the accumulated buffer submits.
/// Hard defaults by design; no config block.
/// </summary>
internal static class VoiceSession
{
    // --- tuning (hard defaults, intentionally not configurable) ---
    private const int SilenceCutMs      = 700;    // trailing silence that closes a speech segment
    private const int AutoSubmitMs      = 2500;   // additional silence after which auto mode submits
    private const int MinSegmentMs      = 300;    // segments shorter than this are dropped as noise
    private const int MaxSegmentMs      = 30000;  // hard cap - force-cut runaway segments
    private const int PreRollMs         = 300;    // audio kept from just before speech onset

    /// <summary>
    /// Mic sensitivity 1..10 (/voice vol). Maps log-scale onto the VAD RMS gate: 1 = least
    /// sensitive (only loud, close speech trips it - noisy rooms), 10 = most sensitive (picks up
    /// quiet/far speech - quiet rooms). Default 5 ~= the original 0.012 gate. Runtime-only by
    /// design (no config persistence).
    /// </summary>
    public static int Sensitivity
    {
        get => _sensitivity;
        set => _sensitivity = Math.Clamp(value, 1, 10);
    }
    private static volatile int _sensitivity = 5;

    /// <summary>The normalized RMS speech gate derived from <see cref="Sensitivity"/>:
    /// level 1 -> 0.040, level 5 -> ~0.014, level 10 -> 0.004 (log interpolation).</summary>
    internal static double VadThreshold => 0.040 * Math.Pow(0.10, (Sensitivity - 1) / 9.0);

    public static VoiceState State { get; private set; } = VoiceState.Off;
    public static bool AutoMode { get; private set; }
    public static bool IsActive => State != VoiceState.Off;
    public static string? LastError { get; private set; }

    private static MicCapture? _mic;
    private static Thread? _worker;
    private static CancellationTokenSource? _cts;
    private static readonly object Gate = new();

    // capture ring buffer (bytes of 16k/16-bit/mono pcm) guarded by Gate
    private static readonly List<byte[]> _pending = new();

    /// <summary>Start (or switch mode on) the app-wide voice session. Returns a user-facing status line.</summary>
    public static string Start(bool auto)
    {
        lock (Gate)
        {
            AutoMode = auto;
            if (IsActive) return $"Voice mode already on{(auto ? " - switched to AUTO submit" : " - manual submit")}.";

            LastError = null;
            State = VoiceState.Warming;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            // Dedicated thread: the loop does blocking waits + subprocess calls (never the pool).
            _worker = new Thread(() => RunLoop(ct)) { IsBackground = true, Name = "mux-voice" };
            _worker.Start();
            return WhisperAssets.IsProvisioned
                ? $"Voice mode ON ({(auto ? "auto-submit on silence" : "manual - say 'send' or press Enter")})."
                : "Voice mode ON - downloading speech assets in background, listening starts when ready...";
        }
    }

    public static string Stop()
    {
        lock (Gate)
        {
            if (!IsActive) return "Voice mode is not on.";
            _cts?.Cancel();
            _mic?.Stop();
            State = VoiceState.Off;
            MuxConsole.TuiRepaintSoon();
            return "Voice mode OFF.";
        }
    }

    private static void SetState(VoiceState s)
    {
        lock (Gate)
        {
            if (State == VoiceState.Off) return;   // stopped underneath the worker - stay off
            if (State == s) return;
            State = s;
        }
        MuxConsole.TuiRepaintSoon();   // the compose indicator reads State on each paint
    }

    private static void Fail(string msg)
    {
        LastError = msg;
        SetState(VoiceState.Error);
        MuxConsole.WriteWarning($"[voice] {msg} - /voice off to dismiss.");
    }

    // ------------------------------------------------------------------ main loop

    private static void RunLoop(CancellationToken ct)
    {
        try
        {
            // 1. Assets (background download on first use; instant when cached).
            var ensure = WhisperAssets.EnsureAsync();
            while (!ensure.IsCompleted)
            {
                if (ct.IsCancellationRequested) return;
                Thread.Sleep(150);
            }
            if (ensure.IsFaulted || !WhisperAssets.IsProvisioned)
            {
                Fail($"speech assets unavailable ({WhisperAssets.LastError ?? "download failed"})");
                return;
            }
            if (ct.IsCancellationRequested) return;

            // 2. Mic.
            var mic = new MicCapture(OnPcm);
            if (!mic.Start())
            {
                Fail($"microphone unavailable ({mic.Error})");
                return;
            }
            lock (Gate) { _mic = mic; }
            SetState(VoiceState.Listening);

            // 3. Segment + transcribe until cancelled.
            SegmentLoop(ct);
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
        finally
        {
            lock (Gate) { _mic?.Stop(); _mic = null; _pending.Clear(); }
        }
    }

    private static void OnPcm(byte[] buf, int n)
    {
        var copy = new byte[n];
        Buffer.BlockCopy(buf, 0, copy, 0, n);
        lock (Gate) { _pending.Add(copy); }
    }

    private static void SegmentLoop(CancellationToken ct)
    {
        const int bytesPerMs = MicCapture.SampleRate * MicCapture.BytesPerSample / 1000;
        var segment = new MemoryStream();
        var preRoll = new Queue<byte[]>();
        int preRollBytes = 0;
        bool inSpeech = false;
        long lastSpeechAt = 0, speechEndedAt = 0;
        bool autoArmed = false;   // true when speech happened since the last submit
        var sw = Stopwatch.StartNew();

        while (!ct.IsCancellationRequested)
        {
            byte[][] frames;
            lock (Gate)
            {
                frames = _pending.ToArray();
                _pending.Clear();
            }
            if (frames.Length == 0)
            {
                // Idle tick: check silence-driven cut/submit deadlines even with no new audio.
                if (inSpeech && sw.ElapsedMilliseconds - lastSpeechAt > SilenceCutMs)
                {
                    CloseSegment();
                }
                else if (AutoMode && autoArmed && !inSpeech
                         && speechEndedAt > 0 && sw.ElapsedMilliseconds - speechEndedAt > AutoSubmitMs
                         && State == VoiceState.Listening)
                {
                    autoArmed = false;
                    MuxConsole.SubmitComposeBuffer();
                }
                Thread.Sleep(30);
                continue;
            }

            foreach (var f in frames)
            {
                bool speech = FrameRms(f) > VadThreshold;
                if (speech)
                {
                    if (!inSpeech)
                    {
                        inSpeech = true;
                        segment.SetLength(0);
                        foreach (var p in preRoll) segment.Write(p);   // keep the onset
                        SetState(VoiceState.Hearing);
                    }
                    lastSpeechAt = sw.ElapsedMilliseconds;
                    segment.Write(f);
                    if (segment.Length > (long)MaxSegmentMs * bytesPerMs) CloseSegment();
                }
                else
                {
                    if (inSpeech)
                    {
                        segment.Write(f);   // trailing silence stays in the segment (natural decay)
                        if (sw.ElapsedMilliseconds - lastSpeechAt > SilenceCutMs) CloseSegment();
                    }
                    else
                    {
                        preRoll.Enqueue(f);
                        preRollBytes += f.Length;
                        while (preRollBytes > PreRollMs * bytesPerMs && preRoll.Count > 0)
                            preRollBytes -= preRoll.Dequeue().Length;
                    }
                }
            }
        }

        void CloseSegment()
        {
            inSpeech = false;
            speechEndedAt = sw.ElapsedMilliseconds;
            preRoll.Clear(); preRollBytes = 0;
            var pcm = segment.ToArray();
            segment.SetLength(0);
            if (pcm.Length < MinSegmentMs * bytesPerMs) { SetState(VoiceState.Listening); return; }

            SetState(VoiceState.Transcribing);
            string text;
            try { text = Transcribe(pcm); }
            catch (Exception ex)
            {
                MuxConsole.WriteWarning($"[voice] transcription failed: {ex.Message}");
                SetState(VoiceState.Listening);
                return;
            }
            SetState(VoiceState.Listening);
            text = CleanTranscript(text);
            if (text.Length == 0) return;

            // Manual-mode voice submit: a chunk that is EXACTLY "send" submits the buffer
            // instead of appending the word. (Auto mode submits on silence instead.)
            if (!AutoMode && IsSendKeyword(text))
            {
                MuxConsole.SubmitComposeBuffer();
                return;
            }

            autoArmed = true;
            MuxConsole.InjectComposeText(text);
        }
    }

    /// <summary>True when the transcript is just the word "send" (whisper often adds punctuation).</summary>
    internal static bool IsSendKeyword(string text)
    {
        var t = text.Trim().TrimEnd('.', '!', '?', ',').Trim().ToLowerInvariant();
        return t == "send";
    }

    /// <summary>Normalized RMS (0..1) of one 16-bit PCM frame.</summary>
    internal static double FrameRms(byte[] pcm)
    {
        if (pcm.Length < 2) return 0;
        double sum = 0;
        int samples = pcm.Length / 2;
        for (int i = 0; i + 1 < pcm.Length; i += 2)
        {
            short s = (short)(pcm[i] | (pcm[i + 1] << 8));
            double v = s / 32768.0;
            sum += v * v;
        }
        return Math.Sqrt(sum / samples);
    }

    /// <summary>Strip whisper artifacts: bracketed non-speech markers and boilerplate blanks.</summary>
    internal static string CleanTranscript(string raw)
    {
        var t = System.Text.RegularExpressions.Regex
            .Replace(raw ?? "", @"[\[\(](?:[^\]\)]*)[\]\)]", " ")   // [BLANK_AUDIO], (wind), [Music]...
            .Trim();
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ");
        return t;
    }

    // ------------------------------------------------------------------ whisper

    private static string Transcribe(byte[] pcm)
    {
        var wav = Path.Combine(Path.GetTempPath(), $"mux-voice-{Guid.NewGuid():N}.wav");
        try
        {
            WriteWav(wav, pcm);
            var psi = new ProcessStartInfo
            {
                FileName = WhisperAssets.CliPath,
                Arguments = $"-m \"{WhisperAssets.ModelPath}\" -f \"{wav}\" -nt -np -t 4",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = WhisperAssets.BinDir,
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start whisper-cli");
            string stdout = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(60_000)) { try { p.Kill(entireProcessTree: true); } catch { } throw new TimeoutException("whisper-cli timed out"); }
            return stdout;
        }
        finally
        {
            try { File.Delete(wav); } catch { }
        }
    }

    private static void WriteWav(string path, byte[] pcm)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        int byteRate = MicCapture.SampleRate * MicCapture.BytesPerSample;
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + pcm.Length);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);                        // PCM
        w.Write((short)1);                        // mono
        w.Write(MicCapture.SampleRate);
        w.Write(byteRate);
        w.Write((short)MicCapture.BytesPerSample); // block align
        w.Write((short)16);                       // bits
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(pcm.Length);
        w.Write(pcm);
    }
}
