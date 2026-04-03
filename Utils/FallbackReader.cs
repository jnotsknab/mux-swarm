namespace MuxSwarm.Utils;

public class FallbackReader(string seedLine) : TextReader
{
    private readonly StringReader _seed = new(seedLine);
    private bool _exhausted;

    public override string? ReadLine()
    {
        if (_exhausted) return Console.In.ReadLine();
        var line = _seed.ReadLine();
        if (line != null) return line;
        _exhausted = true;
        MuxConsole.InputOverride = Console.In;
        return Console.In.ReadLine();
    }

    public override int Read() => _exhausted ? Console.In.Read() : _seed.Read();
}