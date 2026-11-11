using System.Text.RegularExpressions;
using MuxSwarm.Engine.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>A tool result with no announced call (the single-agent TUI path) still shows the tool name.</summary>
[Collection("ConsoleState")]
public class TuiToolNameTests
{
    private sealed class Term : ITuiTerminal
    {
        public int Width { get; set; } = 100;
        public int Height { get; set; } = 20;
        public System.Text.StringBuilder Out { get; } = new();
        public void Write(string text) => Out.Append(text);
        public void Flush() { }
    }

    private static string Plain(string s) => Regex.Replace(s, "\u001b\\[[0-9;?]*[A-Za-z]", "");

    [Fact]
    public void ResultWithoutPendingCall_UsesTheGivenToolName()
    {
        var t = new Term();
        var d = new TuiDriver(t, frameEngine: true);
        d.ResolveMergedToolResult("Status: completed\nCommand: echo hi", toolName: "execute_command_async");
        d.CommitLine("next");   // flushes the settling result
        var rows = d.ComposeFrameRows().Select(Plain).ToList();
        Assert.Contains(rows, r => r.Contains("execute_command_async") && r.Contains("Command: echo hi"));
    }

    [Fact]
    public void PendingCall_StillWinsOverTheFallbackName()
    {
        var d = new TuiDriver(new Term(), frameEngine: true);
        d.BeginToolCall("shell", "ls");
        d.ResolveMergedToolResult("Command: ls", toolName: "other");
        d.CommitLine("next");
        var rows = d.ComposeFrameRows().Select(Plain).ToList();
        Assert.Contains(rows, r => r.Contains("shell (ls)"));
        Assert.DoesNotContain(rows, r => r.Contains("other"));
    }
}
