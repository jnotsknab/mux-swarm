using MuxSwarm.Engine;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>Presentation describes execution styles, not ascending capability tiers.</summary>
public class AgentInterfacePositioningTests
{
    [Fact]
    public void ModeCatalog_PutsMainInterfaceBeforeSerialAndConcurrentSpecialties()
    {
        var agent = Assert.Single(TuiCommands.All, e => e.Cmd == "/agent");
        var swarm = Assert.Single(TuiCommands.All, e => e.Cmd == "/swarm");
        var parallel = Assert.Single(TuiCommands.All, e => e.Cmd == "/pswarm");
        Assert.Contains("main agentic", agent.Desc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("serial", swarm.Desc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("concurrent", parallel.Desc, StringComparison.OrdinalIgnoreCase);
        Assert.True(Array.IndexOf(TuiCommands.All, agent) < Array.IndexOf(TuiCommands.All, swarm));
        Assert.Equal(TuiCommands.Scope.ReplOnly, agent.Scope);
        Assert.Equal(TuiCommands.Scope.ReplOnly, swarm.Scope);
        Assert.Equal(TuiCommands.Scope.ReplOnly, parallel.Scope);
    }

    [Fact]
    public void Help_DistinguishesLeadContextFromSpecialistCapability()
    {
        Assert.Contains("main agentic interface", Help.HelpText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("one specialist at a time", Help.HelpText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("delegated sessions are unchanged", Help.HelpText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("single-agent", Help.HelpText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(180)]
    [InlineData(220)]
    [InlineData(300)]
    public void Splash_MainEntryFirstWithoutDroppingSpecializedModes(int width)
    {
        var rows = MuxConsole.BuildFrameSplashLines("0.14.0", "", "Tip", "hello", width);
        var plain = string.Join("\n", rows.Select(TuiMarkup.Plain));
        Assert.Contains("Main agentic interface", plain);
        Assert.Contains("Specialized execution", plain);
        Assert.True(plain.IndexOf("/agent", StringComparison.Ordinal) < plain.IndexOf("/swarm", StringComparison.Ordinal));
        Assert.Contains("/pswarm", plain); Assert.Contains("/workflow", plain);
        Assert.All(rows, row => Assert.InRange(TuiMarkup.MarkupWidth(row), 0, width - 1));
    }

    [Fact]
    public void AgentPicker_IdentifiesLeadRatherThanImplyingOnlyOneParticipant()
    {
        var agent = new Common.AgentDefinition("CodeAgent", "Code", "unused", true, tools => tools);
        var plain = string.Join("\n", new AgentPickerView(new[] { agent }, agent.Name).Render(90, 20).Select(TuiMarkup.Plain));
        Assert.Contains("CHOOSE LEAD AGENT", plain);
        Assert.Contains("Current lead", plain);
    }
    [Theory]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, true)]
    public void StatusSettingsReportRealFlagsNotImpliedWorkerActivity(bool serial, bool parallel, bool ultra, bool giga)
    {
        string text = CliCmdUtils.DescribeLeadSettings(serial, parallel, ultra, giga);
        Assert.Contains($"serial {(serial ? "on" : "off")}", text);
        Assert.Contains($"parallel {(parallel ? "on" : "off")}", text);
        Assert.Contains($"ultra {(ultra ? "on" : "off")}", text);
        Assert.Contains($"giga {(giga ? "on" : "off")}", text);
        Assert.Contains("not active worker counts", text);
        Assert.Contains("Next /agent", text);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(55)]
    [InlineData(56)]
    [InlineData(57)]
    [InlineData(72)]
    [InlineData(120)]
    [InlineData(179)]
    public void CompactSplashStillOffersMainEntryAndRepoReference(int width)
    {
        var rows = MuxConsole.BuildFrameSplashLines("0.14.0", "", "Tip", "hello", width);
        string text = string.Join("\n", rows.Select(TuiMarkup.Plain));
        Assert.Contains("/agent to start", text);
        Assert.Contains("Check Out The Repo Here!", text);
        // The sub-56 minimal fallback intentionally returns logical rows; the driver wraps them.
        var physical = rows.SelectMany(row => LiveRegion.WrapMarkupLine(row, width - 1)).ToList();
        Assert.All(physical, row => Assert.InRange(TuiMarkup.Width(
            System.Text.RegularExpressions.Regex.Replace(row, "\u001b\\[[0-9;?]*[A-Za-z]", "")), 0, width - 1));
        if (width >= 56) Assert.All(rows, row => Assert.Single(LiveRegion.WrapMarkupLine(row, width - 1)));
    }

    private sealed class SmallTerminal : ITuiTerminal
    {
        public int Width { get; set; } = 72;
        public int Height => 12;
        public void Write(string text) { }
        public void Flush() { }
    }

    [Theory]
    [InlineData(40)]
    [InlineData(72)]
    [InlineData(179)]
    public void ShortFrameKeepsStartHintVisibleAfterStartupComposition(int width)
    {
        var terminal = new SmallTerminal { Width = width };
        var driver = new TuiDriver(terminal, frameEngine: true);
        driver.CommitStartup(w => MuxConsole.BuildFrameSplashLines("0.14.0", "", "Tip", "hello", w));
        string text = System.Text.RegularExpressions.Regex.Replace(string.Join("\n", driver.ComposeFrameRows()), "\u001b\\[[0-9;?]*[A-Za-z]", "");
        Assert.Contains("/agent to start", text);
        Assert.Equal(terminal.Height, driver.ComposeFrameRows().Count);
    }

}
