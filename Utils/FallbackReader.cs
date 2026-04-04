namespace MuxSwarm.Utils;

public class FallbackReader(string seedLine, TextReader? fallback = null) : TextReader
{
    private readonly StringReader _seed = new(seedLine);
    private readonly TextReader _fallback = fallback ?? Console.In;
    private bool _exhausted;

    public override string? ReadLine()
    {
        if (_exhausted) return _fallback.ReadLine();
        var line = _seed.ReadLine();
        _exhausted = true;
        MuxConsole.InputOverride = _fallback;
        if (line != null) return line;
        return _fallback.ReadLine();
    }

    public override int Read() => _exhausted ? _fallback.Read() : _seed.Read();
}