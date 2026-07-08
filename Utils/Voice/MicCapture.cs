using System.Diagnostics;

namespace MuxSwarm.Utils.Voice;

/// <summary>
/// Minimal cross-platform microphone capture producing 16kHz 16-bit mono PCM - the exact input
/// whisper.cpp expects. Windows uses NAudio's WaveInEvent (managed WASAPI/winmm wrapper); Linux
/// spawns arecord (ALSA, present on virtually every desktop distro) and reads raw PCM off its
/// stdout. Buffers are handed to the consumer as-is on a capture-owned thread; the consumer
/// (VoiceSession) does VAD/segmentation. Stop() is idempotent.
/// </summary>
internal sealed class MicCapture : IDisposable
{
    public const int SampleRate = 16000;
    public const int BytesPerSample = 2;

    private readonly Action<byte[], int> _onPcm;
    private NAudio.Wave.WaveInEvent? _waveIn;   // windows
    private Process? _arecord;                  // linux
    private Thread? _pipeThread;
    private volatile bool _running;

    public string? Error { get; private set; }

    public MicCapture(Action<byte[], int> onPcm) => _onPcm = onPcm;

    /// <summary>Begin capture. Returns false (with <see cref="Error"/> set) when no device/backend.</summary>
    public bool Start()
    {
        if (_running) return true;
        try
        {
            if (PlatformContext.IsWindows) StartWindows();
            else if (PlatformContext.IsLinux) StartLinux();
            else { Error = "voice capture is not supported on this OS yet"; return false; }
            _running = true;
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Stop();
            return false;
        }
    }

    private void StartWindows()
    {
        _waveIn = new NAudio.Wave.WaveInEvent
        {
            WaveFormat = new NAudio.Wave.WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 40,
        };
        _waveIn.DataAvailable += (_, e) => { if (_running) _onPcm(e.Buffer, e.BytesRecorded); };
        _waveIn.StartRecording();
    }

    private void StartLinux()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "arecord",
            Arguments = $"-q -f S16_LE -r {SampleRate} -c 1 -t raw -",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        _arecord = Process.Start(psi) ?? throw new InvalidOperationException("failed to start arecord");
        ProcessCleanup.Instance.Track(_arecord);
        // Dedicated thread for the blocking pipe read (never the thread pool - see BRAIN reflexes).
        _pipeThread = new Thread(() =>
        {
            var buf = new byte[3200]; // 100ms
            try
            {
                var s = _arecord.StandardOutput.BaseStream;
                int n;
                while (_running && (n = s.Read(buf, 0, buf.Length)) > 0)
                    _onPcm(buf, n);
            }
            catch { /* stream closed on Stop */ }
        })
        { IsBackground = true, Name = "mux-voice-mic" };
        _pipeThread.Start();
    }

    public void Stop()
    {
        _running = false;
        try { _waveIn?.StopRecording(); _waveIn?.Dispose(); } catch { }
        _waveIn = null;
        try { if (_arecord is { HasExited: false }) _arecord.Kill(entireProcessTree: true); } catch { }
        try { _arecord?.Dispose(); } catch { }
        _arecord = null;
        _pipeThread = null;
    }

    public void Dispose() => Stop();
}
