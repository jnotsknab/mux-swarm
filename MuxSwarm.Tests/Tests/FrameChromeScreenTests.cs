using System.Globalization;
using System.Text.RegularExpressions;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>Cell-state replay of production output, with terminal text-symbol width disagreement.</summary>
public class FrameChromeScreenTests
{
    private sealed class Screen : ITuiTerminal
    {
        public int Width { get; set; } = 40;
        public int Height { get; set; } = 12;
        private string[,] _cells = new string[12, 40];
        private int _row, _column;
        public string Row(int row) => string.Concat(Enumerable.Range(0, Width).Select(c => _cells[row, c] ?? " "));
        public string Gutter => string.Concat(Enumerable.Range(0, Height).Select(r => _cells[r, Width - 1] ?? " "));
        public void Flush() { }
        public void Write(string text)
        {
            if (_cells.GetLength(0) != Height || _cells.GetLength(1) != Width) _cells = new string[Height, Width];
            for (int i = 0; i < text.Length;)
            {
                if (text[i] == '\u001b')
                {
                    var match = Regex.Match(text[i..], "^\u001b\\[([0-9;?]*)([A-Za-z])");
                    Assert.True(match.Success, "Unsupported output escape in cell replay.");
                    string args = match.Groups[1].Value;
                    char command = match.Groups[2].Value[0];
                    if (command == 'H')
                    {
                        var values = args.Length == 0 ? new[] { 1, 1 } : args.Split(';').Select(int.Parse).ToArray();
                        _row = Math.Clamp(values[0] - 1, 0, Height - 1);
                        _column = Math.Clamp(values[1] - 1, 0, Width - 1);
                    }
                    else if (command == 'J' && args == "2") _cells = new string[Height, Width];
                    else if (command == 'K' && (args == "0" || args.Length == 0))
                        for (int c = _column; c < Width; c++) _cells[_row, c] = " ";
                    i += match.Length;
                    continue;
                }
                string element = StringInfo.GetNextTextElement(text, i);
                int width = element == "⎺" ? 1 : TuiMarkup.TextElementWidth(element);
                if (width > 0)
                {
                    _cells[_row, _column] = element;
                    if (width == 2 && _column + 1 < Width) _cells[_row, _column + 1] = "";
                    _column = Math.Min(Width - 1, _column + width); // production DECAWM off
                }
                i += element.Length;
            }
        }
    }

    [Fact]
    public void Rail_MoveHideResizeAndContentMismatch_LeavesExactlyOneContiguousThumb()
    {
        var screen = new Screen();
        var renderer = new FrameRenderer(screen);
        var rows = Enumerable.Repeat("text", screen.Height).ToList();
        void Present(int offset, int total, int room)
        {
            var bar = FrameScrollBar.Create(offset, total, room);
            renderer.Present(rows, Enumerable.Range(0, screen.Height).Select(bar.Cell).ToArray());
            string expected = string.Concat(Enumerable.Range(0, screen.Height).Select(r =>
                !bar.Visible || r >= bar.TrackRows ? ' ' : r >= bar.Top && r < bar.Top + bar.Length ? '█' : '│'));
            Assert.Equal(expected, screen.Gutter);
            for (int r = 0; r < screen.Height; r++)
            {
                string content = screen.Row(r);
                Assert.DoesNotContain('█', content[..^1]);
                Assert.DoesNotContain('│', content[..^1]);
            }
        }
        Present(0, 120, 10);
        rows[8] = new string('⎺', 8); // real width 8, Mux estimate 16
        foreach (int offset in new[] { 20, 80, 110, 30, 0 }) Present(offset, 120, 10);
        Present(1, 120, 4); // footer grows: old rail below it must disappear
        Present(0, 1, 10); // no overflow: no rail
        screen.Width = 60;
        Present(10, 120, 10);
        renderer.Leave();
        Present(0, 120, 10);
    }

    [Fact]
    public void Repaint_WidthMismatch_ClearsLegacyMarkerOutsideEstimatedPadding()
    {
        var screen = new Screen();
        var renderer = new FrameRenderer(screen);
        var rows = Enumerable.Repeat("", screen.Height).ToList();
        rows[2] = new string(' ', 38) + "▏";
        renderer.Present(rows);
        rows[2] = new string('⎺', 18); // actual 18 + estimated pad 4 would leave the marker at 38
        renderer.Present(rows);
        Assert.DoesNotContain('▏', screen.Row(2));
        Assert.Equal(new string('⎺', 18) + new string(' ', 22), screen.Row(2));
    }
}
