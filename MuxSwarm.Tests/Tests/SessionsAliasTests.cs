using MuxSwarm.Engine;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>/sessions keeps the full session list (single + swarm, in the command panel);
/// /history falls back to that list when there is nothing resumable to browse.</summary>
public class SessionsAliasTests
{
    [Fact]
    public void Sessions_ListsAllSessions()
    {
        string body = Between(Read("App.cs"), "case \"/sessions\":", "break;");
        Assert.Contains("CliCmdUtils.ListSessions()", body);
    }

    [Fact]
    public void History_WithNoResumableSessions_FallsBackToTheList()
    {
        string body = Between(Read(Path.Combine("Engine", "CliCmdUtils.cs")), "public static void HandleHistory()", "\n    }");
        Assert.Contains("if (sessions.Count == 0) { ListSessions(); return; }", body);
    }

    private static string Read(string rel) => File.ReadAllText(Path.Combine(SourceRoot(), rel)).Replace("\r\n", "\n");

    private static string Between(string text, string start, string end)
    {
        int i = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(i >= 0, start);
        return text[i..text.IndexOf(end, i + start.Length, StringComparison.Ordinal)];
    }

    private static string SourceRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));
}
