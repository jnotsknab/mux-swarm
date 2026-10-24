using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>Every interactive entry that hands McpTools to an agent session awaits MCP first: the tool list is
/// captured once at session start, so a session started before the servers finish connecting misses their tools.</summary>
public class McpReadyBeforeSessionTests
{
    private static string AppSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "App.cs"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "App.cs"));
    }

    [Theory]
    [InlineData("case \"/agent\":")]
    [InlineData("case \"/stateless\":")]
    [InlineData("case \"/onboard\":")]
    [InlineData("case var rc when rc == \"/resume\"")]
    public void SessionEntry_AwaitsMcp_BeforeHandingOverTools(string caseLabel)
    {
        string src = AppSource();
        int start = src.IndexOf(caseLabel, StringComparison.Ordinal);
        Assert.True(start >= 0, caseLabel);
        int tools = src.IndexOf("mcpTools: McpTools", start, StringComparison.Ordinal);
        Assert.True(tools > start, "no McpTools hand-off after " + caseLabel);
        Assert.Contains("await EnsureMcpReadyAsync();", src[start..tools]);
    }
}
