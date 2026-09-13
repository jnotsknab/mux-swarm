using System.Text;

namespace MuxSwarm.Engine.Tui;

/// <summary>Bounded compatibility inference before editor dispatch. Timers can stage text, never submit it.</summary>
internal sealed class UnframedPasteBuffer
{
    private const int ClassificationMs = 12;
    private const int PasteIdleMs = 40;
    private const int MaxChunkChars = 65536;
    private readonly List<ConsoleInputPump.InputEvent> _pending = new();
    private long _first, _last;
    private bool _likelyPaste, _hasNewline;
    internal bool HasPending => _pending.Count > 0;

    internal IEnumerable<ConsoleInputPump.InputEvent> Feed(ConsoleInputPump.InputEvent ev, long now)
    {
        var result = new List<ConsoleInputPump.InputEvent>();
        bool text = ev.Kind == ConsoleInputPump.EventKind.Key
            && (ev.Key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) == 0
            && (ev.Key.KeyChar is '\r' or '\n' or '\t' || !char.IsControl(ev.Key.KeyChar));
        if (!text)
        {
            result.AddRange(Drain());
            result.Add(ev);
            return result;
        }
        if (HasPending && now - _last >= (_likelyPaste ? PasteIdleMs : ClassificationMs)) result.AddRange(Drain());
        if (!HasPending) _first = now;
        _pending.Add(ev); _last = now;
        // Newlines within a batched text run, or a substantial fast run, are suspicious—not proven paste.
        _hasNewline |= ev.Key.KeyChar is '\r' or '\n';
        if (_pending.Count > 1 && _hasNewline) _likelyPaste = true;
        if (_pending.Count >= 24 && now - _first <= ClassificationMs) _likelyPaste = true;
        if (_pending.Count >= MaxChunkChars) { _likelyPaste = true; result.AddRange(Drain()); }
        return result;
    }

    internal IEnumerable<ConsoleInputPump.InputEvent> Flush(long now)
        => HasPending && now - _last >= (_likelyPaste ? PasteIdleMs : ClassificationMs) ? Drain() : [];

    private List<ConsoleInputPump.InputEvent> Drain()
    {
        var result = new List<ConsoleInputPump.InputEvent>();
        if (_likelyPaste)
        {
            string text = string.Concat(_pending.Select(e => e.Key.KeyChar)).Replace("\r\n", "\n").Replace("\r", "\n");
            result.Add(ConsoleInputPump.InputEvent.OfInferredPaste(text));
        }
        else result.AddRange(_pending);
        _pending.Clear(); _likelyPaste = _hasNewline = false;
        return result;
    }
}
