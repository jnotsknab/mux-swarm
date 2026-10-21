using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Engine;

// Extension host API plumbing: toasts and lines an extension submits as input.
public static partial class MuxConsole
{
    // Lines queued by extensions for the TUI prompt (prompt/submit, command/run): (extension, line).
    private static readonly System.Collections.Concurrent.ConcurrentQueue<(string Ext, string Line)> _injected = new();

    /// <summary>The id of the running lead-agent session, or null outside one.</summary>
    internal static string? CurrentSessionId { get; private set; }

    /// <summary>Where Mux is drawing: <c>tui</c>, <c>classic</c>, <c>web</c> (--serve), <c>stdio</c> or <c>acp</c>.</summary>
    internal static string Surface =>
        AcpActive ? "acp" : ServeMode.IsServing ? "web" : StdioMode ? "stdio" : ViaDriver ? "tui" : "classic";

    /// <summary>
    /// Queue <paramref name="line"/> from extension <paramref name="ext"/> as the next input, read the
    /// way a typed line is: when the session is idle (after the current turn), dispatched normally.
    /// The TUI waits until the prompt is empty, so it never takes over a draft. Returns null when
    /// queued, else why it cannot be (classic renderer, ACP).
    /// </summary>
    internal static string? TryInjectInput(string ext, string line)
    {
        if (AcpActive) return "not available over ACP";
        if (ServeMode.IsServing) { ServeMode.InjectInput(line); return null; }
        if (StdinCancelMonitor.Instance is { } monitor) { monitor.Inject(line); return null; }
        if (!ViaDriver) return "not available in the classic renderer (use the TUI, --serve or --stdio)";
        _injected.Enqueue((ext, line));
        return null;
    }

    /// <summary>Take the next extension-queued line for the TUI prompt, or false when none.</summary>
    internal static bool TryTakeInjected(out string ext, out string line)
    {
        if (_injected.TryDequeue(out var item)) { (ext, line) = item; return true; }
        (ext, line) = ("", "");
        return false;
    }

    /// <summary>
    /// Why extension panels cannot show right now, or null when they can: the TUI (shown from the
    /// idle prompt) with keyboard input.
    /// </summary>
    internal static string? PanelsUnavailable() =>
        ViaDriver && InputOverride == Console.In && ConsoleInputPump.Current is { Disposed: false } ? null
            : $"panels are not available on this surface ({Surface})";

    /// <summary>Drop queued extension input (tests, teardown).</summary>
    internal static void ClearInjected() { while (_injected.TryDequeue(out _)) { } }

    /// <summary>
    /// Show a short notice from extension <paramref name="ext"/>: a transient line above the TUI
    /// footer, an <c>ext_toast</c> event on --serve/--stdio, or an info line in the classic renderer.
    /// </summary>
    internal static void ExtensionToast(string ext, string text)
    {
        if (StdioMode) { WithConsole(() => EmitJson("ext_toast", D(("extension", ext), ("text", text))), clearIndicator: false); return; }
        if (ViaDriver) { lock (ConsoleLock) _driver!.ShowToast($"{ext}: {text}"); return; }
        WriteInfo($"{ext}: {text}");
    }
}
