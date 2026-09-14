using MuxSwarm.Engine;
using MuxSwarm.Engine.Tui;
using Spectre.Console;

namespace MuxSwarm.Tests.Tests;

/// <summary>Deterministic terminal-card layout checks; never launches the live application.</summary>
public class SplashLayoutTests
{
    [Theory]
    [InlineData(180)]
    [InlineData(220)]
    [InlineData(300)]
    public void WidePrimaryCard_LongQuoteCannotStealCommandAndSessionColumns(int width)
    {
        using var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(output),
        });
        console.Profile.Width = width;
        var sessions = Enumerable.Range(0, 5).Select(i =>
            new MuxConsole.SplashSession($"2026-09-12_08-00-{i:D2}", "agent", "session preview")).ToArray();
        console.Write(MuxConsole.BuildSplashRenderable("0.13.2", "", "Quote",
            string.Join(" ", Enumerable.Repeat("a long but valid curated quote", 12)), width, sessions));
        string[] rows = output.ToString().Replace("\r", "").Split('\n');
        Assert.Contains(rows, row => row.Contains("/swarm") && row.Contains("Serial specialists"));
        Assert.Contains(rows, row => row.Contains("2026-09-12_08-00-00"));
        foreach (string row in rows.Where(row => row.Contains("│") && !string.IsNullOrWhiteSpace(row.Trim('│', ' '))))
            Assert.Equal(4, row.Count(c => c == '│'));
    }

    [Fact]
    public void FrameCard_WidthSweep_PreservesBordersAndBrandWithoutPhysicalRewrap()
    {
        var sessions = new[] { new MuxConsole.SplashSession("session", "agent", "漢字 e\u0301 👩‍💻 [[literal]]\nsecond\tline\u001b[2J") };
        foreach (int width in new[] { 56, 57, 72, 120, 179, 180, 181, 220, 300, 500 })
        {
            var rows = MuxConsole.BuildFrameSplashLines("0.13.2", "debug", "Quote",
                string.Join(" ", Enumerable.Repeat("a bounded quote", 40)), width, sessions);
            string plain = string.Join("\n", rows.Select(TuiMarkup.Plain));
            Assert.Contains("███╗", plain);
            Assert.Contains("◠ ◠", plain);
            Assert.Contains("╚══════╝", plain);
            foreach (string row in rows)
            {
                Assert.InRange(TuiMarkup.MarkupWidth(row), 0, width - 1);
                Assert.Single(LiveRegion.WrapMarkupLine(row, width - 1));
                Assert.DoesNotContain('\u001b', TuiMarkup.Plain(row));
            }
            if (width >= 180)
            {
                Assert.Contains("second", plain);
                Assert.Contains("line", plain);
                var body = rows.Select(TuiMarkup.Plain).Where(r => r.Contains("Getting Started")).Single();
                Assert.Equal(4, body.Count(c => c == '│'));
            }
        }
    }

    private sealed class Terminal : ITuiTerminal
    {
        public int Width { get; set; } = 220;
        public int Height => 60;
        public void Write(string text) { }
        public void Flush() { }
    }

    [Fact]
    public void FrameStartup_ResizeRebuildsSemanticCardInsteadOfWrappingOldBorders()
    {
        var terminal = new Terminal();
        var driver = new TuiDriver(terminal, frameEngine: true);
        var widths = new List<int>();
        driver.CommitStartup(width =>
        {
            widths.Add(width);
            return MuxConsole.BuildFrameSplashLines("0.13.2", "", "Tip", "hello", width);
        });
        foreach (int width in new[] { 220, 72, 180, 56, 240 })
        {
            terminal.Width = width;
            var rows = driver.ComposeFrameRows();
            Assert.Contains(width, widths);
            Assert.Equal(terminal.Height, rows.Count);
            Assert.Contains(rows, row => row.Contains("╭") && row.Contains("╮"));
            Assert.Contains(rows, row => row.Contains("╰") && row.Contains("╯"));
            Assert.Equal(width >= 180, rows.Any(row => System.Text.RegularExpressions.Regex.Replace(row, "\u001b\\[[0-9;?]*[A-Za-z]", "").Contains("Getting Started")));
        }
    }
}
