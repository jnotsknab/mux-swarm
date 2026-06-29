using System.Collections.Concurrent;
using System.Text;

namespace MuxSwarm.Utils.Acp;

/// <summary>
/// The TextReader that feeds an ACP-driven session into the existing interactive REPL.
/// Installed as <see cref="MuxConsole.InputOverride"/> while an ACP session is live, exactly
/// as <c>ServeMode</c> installs its WebSocket-backed reader.
///
/// Two facts make this the whole turn-driver with no orchestrator surgery:
/// <list type="number">
/// <item>The single-agent loop reads the next user goal via <c>MuxConsole.ReadInput</c> ->
///   <c>InputOverride.ReadLine()</c>. When that call is ENTERED, the previous turn has fully
///   completed (the stream ended, the session persisted). So entering <see cref="ReadLine"/>
///   is the exact "turn boundary" signal an ACP adapter needs to answer <c>session/prompt</c>
///   with a <c>stopReason</c>.</item>
/// <item>Returning a goal string drives the next turn; returning <c>/qc</c> ends the session
///   loop cleanly. We never return null or whitespace (whitespace is treated as quit by the
///   orchestrator).</item>
/// </list>
/// </summary>
public sealed class AcpInputReader : TextReader
{
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>());
    private volatile bool _closed;

    /// <summary>
    /// Raised at the start of every <see cref="ReadLine"/>. The server uses this as the
    /// turn-boundary tick: the first tick means "ready for the first prompt"; every later tick
    /// means "the prompt that was in flight has finished".
    /// </summary>
    public event Action? ReadEntered;

    /// <summary>Enqueue the next line the REPL should consume (a goal, or a control command).</summary>
    public void Push(string line) => _queue.Add(line);

    /// <summary>Signal end-of-session: unblock any pending read with /qc and stop accepting input.</summary>
    public void CloseSession()
    {
        if (_closed) return;
        _closed = true;
        _queue.Add("/qc");
    }

    public override string? ReadLine()
    {
        ReadEntered?.Invoke();
        if (_closed && _queue.Count == 0) return "/qc";
        try
        {
            return _queue.Take();
        }
        catch (InvalidOperationException)
        {
            return "/qc";
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closed = true;
            try { _queue.CompleteAdding(); } catch { /* ignore */ }
            _queue.Dispose();
        }
        base.Dispose(disposing);
    }
}
