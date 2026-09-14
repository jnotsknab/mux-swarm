using System.Text;

namespace MuxSwarm.Engine.Tui;

/// <summary>Negotiated OSC 5522 clipboard transactions. Transport parsing remains on the single input pump.</summary>
internal sealed class TerminalPasteProtocol
{
    internal const string Query = "\x1b[?5522$p";
    internal const string Disable = "\x1b[?5522l";
    internal const int MaxPacketChars = 32768;
    private readonly Action<string> _write;
    private readonly Action<ClipboardCapture.Payload> _deliver;
    private readonly MemoryStream _data = new();
    private readonly StringBuilder _listing = new();
    private string? _mime, _password, _location;
    private bool _reading, _enabled, _queryPending, _discardUntilDone;
    private long _started;
    private long _last, _transactionStart;
    internal bool Busy { get; private set; }
    internal bool Enabled => _enabled;

    internal TerminalPasteProtocol(Action<string> write, Action<ClipboardCapture.Payload> deliver)
    { _write = write; _deliver = deliver; }

    internal void Start() { Reset(); _discardUntilDone = false; _enabled = false; _queryPending = true; _started = Environment.TickCount64; _write(Query); }
    internal void Stop() { if (_enabled) _write(Disable); _enabled = _queryPending = false; Reset(); }
    internal void Cancel() { _discardUntilDone = Busy; Reset(); }
    private void Reset() { Busy = _reading = false; _mime = _password = _location = null; _data.SetLength(0); _listing.Clear(); }

    internal void Tick()
    {
        if (Busy && (Environment.TickCount64 - _last > 5000 || Environment.TickCount64 - _transactionStart > 20000)) Fail("Image paste timed out; retry paste or use a readable file path.");
    }
    private void Fail(string message) { _discardUntilDone = Busy; Reset(); _deliver(new(Error: message)); }

    internal void Handle(string packet)
    {
        if (packet.StartsWith("\x1b[?5522;", StringComparison.Ordinal) && packet.EndsWith("$y", StringComparison.Ordinal))
        {
            if (!_queryPending || Environment.TickCount64 - _started > 2000) return;
            _queryPending = false;
            var status = packet[8..].Split('$')[0];
            if (status is "1" or "2") { _enabled = true; _write("\x1b[?5522h"); }
            return;
        }
        if (!_enabled || !packet.StartsWith("\x1b]5522;", StringComparison.Ordinal)) return;
        try
        {
            var body = packet[7..].TrimEnd('\a');
            if (body.EndsWith("\x1b\\")) body = body[..^2];
            var halves = body.Split(';', 2);
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var part in halves[0].Split(':'))
            { int eq = part.IndexOf('='); if (eq > 0) fields[part[..eq]] = part[(eq + 1)..]; }
            if (fields.GetValueOrDefault("type") != "read") return;
            string? status = fields.GetValueOrDefault("status");
            if (_discardUntilDone) { if (status == "DONE") _discardUntilDone = false; return; }
            _last = Environment.TickCount64;
            if (status == "OK")
            {
                if (!_reading) { Reset(); Busy = true; _transactionStart = Environment.TickCount64; _password = fields.GetValueOrDefault("pw"); _location = fields.GetValueOrDefault("loc"); }
                return;
            }
            if (!Busy) return;
            if (status == "DATA")
            {
                string mime = Encoding.UTF8.GetString(Convert.FromBase64String(fields.GetValueOrDefault("mime") ?? ""));
                byte[] bytes = Convert.FromBase64String(halves.Length > 1 ? halves[1] : "");
                if (bytes.Length > 8192) throw new IOException("Enhanced paste chunk too large.");
                if (!_reading && mime == ".")
                {
                    if (_listing.Length + bytes.Length > 8192) throw new IOException("Clipboard MIME list too large.");
                    _listing.Append(Encoding.UTF8.GetString(bytes));
                }
                else if (_reading && mime == _mime)
                {
                    if (_data.Length + bytes.Length > ClipboardCapture.MaxBytes) throw new IOException("Paste exceeds 16 MiB.");
                    _data.Write(bytes);
                }
                return;
            }
            if (status == "DONE")
            {
                if (!_reading)
                {
                    var offered = _listing.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    _mime = new[] { "image/png", "image/jpeg", "image/webp", "image/gif", "image/bmp", "text/plain" }.FirstOrDefault(offered.Contains);
                    if (_mime is null) { Fail("Clipboard has no supported text or image format."); return; }
                    string metadata = "type=read";
                    if (_location == "primary") metadata += ":loc=primary";
                    if (!string.IsNullOrEmpty(_password))
                    {
                        // Validate encoded token before echoing metadata. Never let a terminal reply inject controls.
                        _ = Convert.FromBase64String(_password);
                        if (_password.Length > 1024 || _password.Any(char.IsControl)) throw new IOException("Invalid clipboard token.");
                        metadata += ":pw=" + _password + ":name=UGFzdGUgZXZlbnQ=";
                    }
                    _reading = true; _data.SetLength(0);
                    _write("\x1b]5522;" + metadata + ";" + Convert.ToBase64String(Encoding.UTF8.GetBytes(_mime)) + "\x1b\\");
                    return;
                }
                byte[] result = _data.ToArray(); bool text = _mime == "text/plain"; Reset();
                _deliver(text ? new(Text: Encoding.UTF8.GetString(result)) : new(Image: result));
                return;
            }
            if (status is not null) Fail("Enhanced paste unavailable: " + status);
        }
        catch (Exception ex) when (ex is FormatException or IOException or ArgumentException)
        { Fail("Invalid enhanced paste: " + ex.Message); }
    }
}
