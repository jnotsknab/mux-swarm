using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Engine;

public sealed class ThinkingIndicator : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string> _renderRaw;
    private readonly Action<int> _clearLine;
    private readonly object _consoleLock;
    private readonly Action<string>? _onStatusUpdate;
    // Cumulative tool-call COUNT observer (fires with toolCalls.Count on each list update): the
    // capture lane consumes this so its counter is derived from the SAME source as the
    // "[calling: ...]" labels and the two can never diverge.
    private readonly Action<int>? _onToolCalls;
    private readonly Action? _onDispose;

    private volatile string _status = "thinking";
    // True once an orchestrator calls UpdateStatus(string) with a concrete status; until then
    // the loop free-cycles quirky quips instead of showing the literal default.
    private volatile bool _statusExplicit;
    private List<string>? _toolCalls;

    private int _disposed;

    private int _maxLen;
    private volatile bool _hasRendered;

    private readonly ManualResetEventSlim _loopExited = new(true);
    private volatile bool _clearedExternally;

    private static readonly string[] BuiltInFrames =
        ["⠀", "⠄", "⠆", "⠦", "⠶", "⠷", "⣷", "⣿", "⣷", "⠷", "⠶", "⠦", "⠆", "⠄", "⠀"];

    // Quirky Claude-Code-style "working" gerunds, cycled (~every 2s) while the agent is
    // reasoning and no tool call is in flight. Purely cosmetic flavor for the live spinner.
    private static readonly string[] BuiltInQuips =
    [
        "Thinking", "Cogitating", "Percolating", "Ruminating", "Conjuring",
        "Noodling", "Finagling", "Marinating", "Crystallizing", "Pondering",
        "Scheming", "Tinkering", "Untangling", "Synthesizing", "Brewing",
        "Wrangling", "Mulling", "Deliberating", "Spelunking", "Computing",
        "Vibing", "Plotting", "Cooking", "Simmering", "Incubating",
        "Calibrating", "Orchestrating", "Triangulating", "Distilling", "Fermenting",
        "Hypothesizing", "Theorizing", "Whirring", "Churning", "Processing",
        "Divining", "Channeling", "Manifesting", "Summoning", "Weaving",
        "Reticulating", "Bamboozling", "Galvanizing", "Concocting", "Hatching",
        "Architecting", "Strategizing", "Contemplating", "Wibbling", "Buffering",
        "Forging", "Sculpting", "Tessellating", "Extrapolating", "Improvising",
    ];

    // Per-user, shuffled rotation of the quip pool: each user gets their OWN stable shuffle order
    // (seeded from machine + user identity), so the gerunds are randomized rather than always
    // cycling Thinking->Cogitating->... in array order, and two users see different sequences. The
    // shuffle is computed once (lazy) and reused; deterministic per user so it is unit-testable.
    private static string[]? _rotation;
    private static readonly object _rotationLock = new();

    // Extension overrides (ExtensionRegistry). Null = built-ins. Swapped as whole arrays, never mutated.
    private static string[]? _extensionFrames;
    private static string[]? _extensionQuips;
    private static volatile Timing _timing = Timing.Default;

    /// <summary>Animation timing and quip suffix of the thinking indicator.</summary>
    internal sealed record Timing(int IntervalMs, int QuipIntervalMs, bool Shuffle, string Suffix)
    {
        /// <summary>The built-in timing: 80ms frames, a new quip every 2s, shuffled, "..." suffix.</summary>
        internal static readonly Timing Default = new(80, 2000, true, "...");
    }

    /// <summary>The active timing (built-in unless an extension set a <c>thinking</c> block).</summary>
    internal static Timing ActiveTiming => _timing;

    /// <summary>Spinner frames contributed by an extension, or null when the built-ins are in use.</summary>
    internal static IReadOnlyList<string>? ExtensionFrames => _extensionFrames;

    /// <summary>
    /// Replace the spinner frames, quip pool and timing (null = built-in). Called by the extension
    /// loader. Frames narrower than the widest are padded with braille blanks (one cell, not
    /// whitespace, so line layout never collapses them) so the indicator does not jitter.
    /// </summary>
    internal static void SetExtensionCosmetics(string[]? frames, string[]? quips, Timing? timing = null)
    {
        _extensionFrames = frames is null ? null : PadFrames(frames);
        _timing = timing ?? Timing.Default;
        lock (_rotationLock)
        {
            _extensionQuips = quips;
            _rotation = null;   // rebuilt from the new pool on next use
        }
    }

    /// <summary>Pad every frame on the right to the widest frame's cell width with U+2800.</summary>
    internal static string[] PadFrames(string[] frames)
    {
        int widest = frames.Max(f => Tui.TuiMarkup.Width(f));
        return frames.Select(f => f + new string('\u2800', widest - Tui.TuiMarkup.Width(f))).ToArray();
    }

    internal static int UserSeed()
    {
        // Stable per-user/machine seed. Best-effort: any failure falls back to a fixed seed so the
        // rotation is still randomized (just not user-specific).
        try
        {
            string id = (Environment.UserName ?? "") + "@" + (Environment.MachineName ?? "");
            return StableHash(id);
        }
        catch { return 0x5EED; }
    }

    // FNV-1a 32-bit: stable across runs/platforms (string.GetHashCode is randomized per process).
    private static int StableHash(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return (int)h;
        }
    }

    /// <summary>The per-user shuffled quip rotation (built once). Pure given <see cref="UserSeed"/>.</summary>
    internal static string[] Rotation()
    {
        var r = _rotation;
        if (r != null) return r;
        lock (_rotationLock)
        {
            if (_rotation != null) return _rotation;
            var pool = _extensionQuips ?? BuiltInQuips;
            _rotation = _timing.Shuffle ? Shuffle(pool, UserSeed()) : (string[])pool.Clone();
            return _rotation;
        }
    }

    /// <summary>Deterministic Fisher-Yates shuffle of <paramref name="src"/> seeded by
    /// <paramref name="seed"/>. Pure - same seed always yields the same order (testable).</summary>
    internal static string[] Shuffle(string[] src, int seed)
    {
        var a = (string[])src.Clone();
        var rng = new Random(seed);
        for (int i = a.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (a[i], a[j]) = (a[j], a[i]);
        }
        return a;
    }


    internal ThinkingIndicator(
        Action<string> renderRaw,
        Action<int> clearLine,
        object consoleLock,
        Action<string>? onStatusUpdate = null,
        Action? onDispose = null,
        Action<int>? onToolCalls = null)
    {
        _renderRaw = renderRaw;
        _clearLine = clearLine;
        _consoleLock = consoleLock;
        _onStatusUpdate = onStatusUpdate;
        _onDispose = onDispose;
        _onToolCalls = onToolCalls;
    }

    internal bool HasRendered => _hasRendered;
    internal int MaxLen => _maxLen;

    public void UpdateStatus(string status)
    {
        lock (_consoleLock)
        {
            _toolCalls = null;
        }
        _status = status;
        _statusExplicit = true;
        _onStatusUpdate?.Invoke(status);
    }

    public void UpdateStatus(IReadOnlyList<string> toolCalls)
    {
        lock (_consoleLock)
        {
            _toolCalls = new List<string>(toolCalls);
        }
        // Show human action labels (verb-derived) instead of raw tool ids in the live indicator,
        // e.g. "[calling: Running command, Sleeping]" not the raw "ReplShellMcp_execute_command_async".
        var labels = toolCalls.Select(ToolActionLabel.Describe);
        _onStatusUpdate?.Invoke($"[calling: {string.Join(", ", labels)}]");
        _onToolCalls?.Invoke(toolCalls.Count);
    }

    private static int SafeWindowWidth()
    {
        try { return Console.WindowWidth; }
        catch { return 120; }
    }

    private string BuildToolCallStatus(int budget)
    {
        // _consoleLock must be held by caller
        var calls = _toolCalls?.Select(ToolActionLabel.Describe).ToList();
        if (calls == null || calls.Count == 0)
            return _status + "...";

        const string prefix = "[calling: ";
        const string suffix = "]...";
        const string separator = ", ";

        int overhead = prefix.Length + suffix.Length;
        int remaining = budget - overhead;

        if (remaining <= 0)
            return _status + "...";

        // Walk from tail, newest first, accumulate what fits
        var visible = new List<string>();
        int used = 0;

        for (int i = calls.Count - 1; i >= 0; i--)
        {
            int cost = calls[i].Length + (visible.Count > 0 ? separator.Length : 0);
            if (used + cost > remaining)
                break;
            visible.Add(calls[i]);
            used += cost;
        }

        visible.Reverse();
        return $"{prefix}{string.Join(separator, visible)}{suffix}";
    }

    internal void Start(string agentName)
    {
        _loopExited.Reset();
        _ = Task.Run(async () =>
        {
            try
            {
                int frame = 0;
                var frames = _extensionFrames ?? BuiltInFrames;
                var timing = _timing;

                while (!_cts.Token.IsCancellationRequested)
                {
                    string spinner = frames[frame++ % frames.Length];
                    string linePrefix = $"  {spinner} {agentName} ";

                    int maxWidth = SafeWindowWidth() - 1;
                    int budget = maxWidth - linePrefix.Length;

                    string statusPart;
                    lock (_consoleLock)
                    {
                        if (_toolCalls != null)
                        {
                            statusPart = BuildToolCallStatus(budget);
                        }
                        else if (_statusExplicit)
                        {
                            // An orchestrator set a specific status string; honor it verbatim.
                            statusPart = _status + "...";
                        }
                        else
                        {
                            // Default reasoning state: cycle a quirky gerund every ~2s, walking the
                            // PER-USER shuffled rotation (randomized order, stable per user).
                            var rot = Rotation();
                            statusPart = rot[(int)((long)frame * timing.IntervalMs / timing.QuipIntervalMs % rot.Length)] + timing.Suffix;
                        }
                    }

                    string line = linePrefix + statusPart;

                    if (line.Length > maxWidth)
                        line = line[..(maxWidth - 3)] + "...";

                    int len = line.Length;
                    if (len > _maxLen) _maxLen = len;

                    string padded = line.PadRight(_maxLen);

                    lock (_consoleLock)
                    {
                        if (!_cts.Token.IsCancellationRequested)
                        {
                            _hasRendered = true;
                            _renderRaw("\r" + padded);
                        }
                    }

                    try { await Task.Delay(timing.IntervalMs, _cts.Token); }
                    catch (OperationCanceledException) { break; }
                }

                if (!_clearedExternally)
                {
                    lock (_consoleLock)
                    {
                        if (!_clearedExternally && _maxLen > 0)
                            _clearLine(_maxLen);
                    }
                }
            }
            finally
            {
                _loopExited.Set();
            }
        });
    }

    internal void ClearNow()
    {
        lock (_consoleLock)
        {
            if (_maxLen > 0)
            {
                _clearLine(_maxLen);
                _clearedExternally = true;
            }
        }
    }

    internal void ClearNow_NoLock()
    {
        if (_maxLen > 0)
        {
            _clearLine(_maxLen);
            _clearedExternally = true;
        }
    }

    internal void CancelNoWait()
    {
        try { _cts.Cancel(); }
        catch { /* ignore */ }
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try { _cts.Cancel(); }
            catch { /* ignore */ }

            _loopExited.Wait(TimeSpan.FromMilliseconds(500));

            _onDispose?.Invoke();

            _cts.Dispose();
            _loopExited.Dispose();
        }
    }

    public void Dispose() => Stop();
}