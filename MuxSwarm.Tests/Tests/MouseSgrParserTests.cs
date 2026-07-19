using MuxSwarm.Utils.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Covers MouseSgrParser: parsing the body of an SGR mouse report (ESC[&lt;b;x;y M/m) into
/// button/x/y, wheel-direction classification, and strict rejection of malformed bodies (the
/// anti-spillover guarantee - a torn/garbage body must NOT parse into a phantom scroll).
/// </summary>
public class MouseSgrParserTests
{
    [Theory]
    [InlineData("64;10;20", 64, 10, 20)]   // wheel up
    [InlineData("65;1;1", 65, 1, 1)]       // wheel down
    [InlineData("0;80;24", 0, 80, 24)]     // left button press
    [InlineData("35;120;40", 35, 120, 40)] // motion
    public void TryParseBody_ValidBody_Parses(string body, int b, int x, int y)
    {
        Assert.True(MouseSgrParser.TryParseBody(body, out int button, out int px, out int py));
        Assert.Equal(b, button);
        Assert.Equal(x, px);
        Assert.Equal(y, py);
    }

    [Theory]
    [InlineData("")]            // empty
    [InlineData("64")]          // too few fields
    [InlineData("64;10")]       // two fields
    [InlineData("64;10;20;5")]  // too many fields
    [InlineData("64;;20")]      // empty middle field
    [InlineData(";10;20")]      // empty first field
    [InlineData("64;10;")]      // empty trailing field
    [InlineData("6a;10;20")]    // non-digit
    [InlineData("64;1 0;20")]   // embedded space
    [InlineData("<64;10;20")]   // stray prefix char leaked into body
    public void TryParseBody_Malformed_Rejected(string body)
    {
        Assert.False(MouseSgrParser.TryParseBody(body, out _, out _, out _));
    }

    [Theory]
    [InlineData(64, +1)]    // wheel up  -> back into history
    [InlineData(65, -1)]    // wheel down -> toward live tail
    [InlineData(64 + 4, +1)] // wheel up + shift modifier bit (low 7 bits still 64)
    [InlineData(0, 0)]      // left click - not a wheel
    [InlineData(32, 0)]     // motion - not a wheel
    [InlineData(2, 0)]      // right click - not a wheel
    public void WheelDirection_ClassifiesWheelButtons(int button, int expected)
    {
        Assert.Equal(expected, MouseSgrParser.WheelDirection(button));
    }

    [Fact]
    public void WheelDirection_HigherModifierBitsIgnored()
    {
        // Ctrl/drag/motion modifier bits sit above the low 7; masking must still see the wheel code.
        Assert.Equal(+1, MouseSgrParser.WheelDirection(64 | 0x80));
        Assert.Equal(-1, MouseSgrParser.WheelDirection(65 | 0x80));
    }
}
