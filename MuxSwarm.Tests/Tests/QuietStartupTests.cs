using MuxSwarm.Engine;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>Quiet startup: routine notices go to the /logs notice log, warnings and errors still print.</summary>
[Collection("ConsoleState")]
public class QuietStartupTests
{
    [Fact]
    public void QuietStartup_HoldsRoutineNotices_ButLogsThemAll_AndPrintsWarnings()
    {
        var prior = Spectre.Console.AnsiConsole.Console;
        var output = new StringWriter();
        Spectre.Console.AnsiConsole.Console = Spectre.Console.AnsiConsole.Create(new Spectre.Console.AnsiConsoleSettings
        {
            Ansi = Spectre.Console.AnsiSupport.No, ColorSystem = Spectre.Console.ColorSystemSupport.NoColors,
            Out = new Spectre.Console.AnsiConsoleOutput(output),
        });
        string id = Guid.NewGuid().ToString("N")[..8];
        try
        {
            MuxConsole.QuietStartup = true;
            MuxConsole.WriteInfo($"provider {id}");
            MuxConsole.WriteSuccess($"skills {id}");
            MuxConsole.WriteMuted($"skip {id}");
            MuxConsole.WriteWarning($"careful {id}");
            MuxConsole.EndQuietStartup();
            MuxConsole.WriteInfo($"after {id}");
        }
        finally
        {
            MuxConsole.QuietStartup = false;
            Spectre.Console.AnsiConsole.Console = prior;
        }
        string shown = output.ToString();
        Assert.DoesNotContain($"provider {id}", shown);
        Assert.DoesNotContain($"skills {id}", shown);
        Assert.DoesNotContain($"skip {id}", shown);
        Assert.Contains($"careful {id}", shown);
        Assert.Contains($"after {id}", shown);
        var log = MuxConsole.NoticeLog.Where(n => n.Text.EndsWith(id, StringComparison.Ordinal)).Select(n => n.Level).ToList();
        Assert.Equal(["info", "ok", "muted", "warn", "info"], log);
        Assert.Contains(LogsCommand.Rows(1), r => r.Contains($"provider {id}"));
    }
}
