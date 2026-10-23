using System.Text;
using MuxSwarm.Engine.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>Copying TUI text keeps non-ASCII glyphs (box rules, chevrons, ellipses) intact on every path.</summary>
public class TuiClipboardEncodingTests
{
    private const string Sample = "\u2500\u2500 Recent \u203a 2026-10-22 \u2026 \u26a0 \u6f22\u5b57";

    [Fact]
    public void ClipExePayload_IsUtf16WithBom_AndRoundTrips()
    {
        byte[] b = TuiClipboard.ClipExePayload(Sample);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, b[..2]);
        Assert.Equal(Sample, Encoding.Unicode.GetString(b, 2, b.Length - 2));
        Assert.NotEqual(Encoding.UTF8.GetBytes(Sample), b);   // UTF-8 without a BOM is what clip.exe misreads
    }

    [Fact]
    public void Osc52_CarriesUtf8_RoundTrip()
    {
        string seq = TuiClipboard.Osc52(Sample);
        string b64 = seq[(seq.IndexOf(";c;", StringComparison.Ordinal) + 3)..^1];
        Assert.Equal(Sample, Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
    }
}
