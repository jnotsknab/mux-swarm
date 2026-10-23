using MuxSwarm.Engine;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>/sessions is an alias of /history; outside the TUI both print the session list.</summary>
public class SessionsAliasTests
{
    [Fact]
    public void Sessions_DispatchesToHistory()
    {
        string app = File.ReadAllText(Path.Combine(SourceRoot(), "App.cs")).Replace("\r\n", "\n");
        int i = app.IndexOf("case \"/sessions\":", StringComparison.Ordinal);
        Assert.True(i > 0);
        string body = app[i..app.IndexOf("break;", i, StringComparison.Ordinal)];
        Assert.Contains("CliCmdUtils.HandleHistory()", body);
        Assert.DoesNotContain("ListSessions", body);
    }

    private static string SourceRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));
}
