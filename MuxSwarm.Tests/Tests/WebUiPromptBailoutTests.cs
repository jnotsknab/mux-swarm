using MuxSwarm.Engine;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// ask_user bailout rows in the web UI: "enter custom" sends "=text" (not its row index, which the
/// engine read as a literal custom answer such as "5"), and "cancel" sends "cancel".
/// </summary>
public class WebUiPromptBailoutTests
{
    private static readonly string Html = File.ReadAllText(Path.Combine(RepoRoot(), "Runtime", "mux-web-app", "index.html"));

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "MuxSwarm.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException("MuxSwarm.csproj not found above the test output");
    }

    [Fact]
    public void BailoutRows_AreSplitOff_AndSendTheEngineProtocol()
    {
        Assert.Contains("if (ev.bailout && choices.length >= 2) {", Html);
        Assert.Contains("sendRaw(picked.length ? picked.join(',') + ',=' + v : '=' + v);", Html);
        Assert.Contains("sendRaw('cancel'); done('cancelled');", Html);
        Assert.Contains("respond('cancel', 'cancelled')", Html);
    }

    [Fact]
    public void MultiSelectCustom_KeepsTheCheckedOptions()
    {
        // Multi-select "enter custom" sends the checked indices with the text ("1,3,=text"), not the text alone.
        Assert.Contains("const picked = multi ? Array.from(selected).sort((a, b) => a - b) : [];", Html);
    }

    [Fact]
    public void Engine_SelectRequest_PutsTheTwoBailoutRowsLast()
    {
        // The web UI drops exactly the last two emitted choices; pin the engine's emit order.
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "Engine", "MuxConsole.cs"));
        Assert.Contains("new List<string>(list) { CustomAffordanceLabel, CancelAffordanceLabel };", src);
    }
}
