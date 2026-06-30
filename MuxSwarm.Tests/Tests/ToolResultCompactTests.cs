using System.Linq;
using MuxSwarm.Utils.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// The collapsed tool-result preview (Ctrl+T strip / one-line summary) must show WHAT ran, not the
// bookkeeping status, for shell + Python REPL dispatches. ToolResultCompact picks the Command:/Code:
// line over the leading Job ID:/Status: lines.
public class ToolResultCompactTests
{
    private static string PlainText(System.Collections.Generic.List<string> rendered)
        => string.Join("\n", rendered);

    [Fact]
    public void Compact_PythonRepl_SurfacesCodeLine_NotStatus()
    {
        // Mirrors ReplSession.RenderResult output shape.
        var result = "Status: completed\nCode: import os; print(os.getcwd())\n\n--- STDOUT ---\n/home/x";
        var rendered = PlainText(TuiComponents.ToolResultCompact(result));
        Assert.Contains("import os", rendered);
        Assert.DoesNotContain("Status: completed", rendered);
    }

    [Fact]
    public void Compact_PythonRepl_MultiLine_ShowsFirstLineAndMoreMarker()
    {
        var result = "Status: completed\nCode: x = 1  (+3 more lines)\n\n--- STDOUT ---\n4";
        var rendered = PlainText(TuiComponents.ToolResultCompact(result));
        Assert.Contains("x = 1", rendered);
        Assert.Contains("+3 more", rendered);
    }

    [Fact]
    public void Compact_AsyncShell_StillSurfacesCommandLine()
    {
        var result = "Job ID: abc123\nStatus: running\nCommand: git status";
        var rendered = PlainText(TuiComponents.ToolResultCompact(result));
        Assert.Contains("git status", rendered);
        Assert.DoesNotContain("abc123", rendered);
    }

    [Fact]
    public void Compact_PlainResult_FallsBackToFirstLine()
    {
        var result = "Done. File saved.";
        var rendered = PlainText(TuiComponents.ToolResultCompact(result));
        Assert.Contains("Done. File saved.", rendered);
    }
}
