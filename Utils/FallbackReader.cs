namespace MuxSwarm.Utils;

/// <summary>
/// A TextReader that injects a single seeded "first input" before falling back to the real input
/// (Console.In by default). Used to auto-deliver a one-shot task to an interactive agent session
/// (e.g. the /newagent and /createhook helper flows).
///
/// IMPORTANT: the seed is delivered WHOLE on the first ReadLine() - any internal newlines are
/// flattened to spaces so a multi-line brief is not truncated to its first line (the consumer reads
/// one logical line per turn). After the seed is consumed, all reads pass through to the fallback.
/// </summary>
public class FallbackReader : TextReader
{
    private readonly string _seedFlattened;
    private readonly TextReader _fallback;
    private bool _exhausted;
    private StringReader? _seedChars;

    public FallbackReader(string seedLine, TextReader? fallback = null)
    {
        // Flatten any newlines so the entire seed survives a single ReadLine() call.
        _seedFlattened = (seedLine ?? string.Empty)
            .Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
        _fallback = fallback ?? Console.In;
        _seedChars = new StringReader(_seedFlattened);
    }

    public override string? ReadLine()
    {
        if (_exhausted) return _fallback.ReadLine();
        _exhausted = true;
        MuxConsole.InputOverride = _fallback;
        return _seedFlattened.Length > 0 ? _seedFlattened : _fallback.ReadLine();
    }

    public override int Read()
    {
        if (_exhausted) return _fallback.Read();
        int c = _seedChars!.Read();
        if (c == -1)
        {
            _exhausted = true;
            MuxConsole.InputOverride = _fallback;
            return _fallback.Read();
        }
        return c;
    }
}
