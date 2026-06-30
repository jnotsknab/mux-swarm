using System.Linq;
using MuxSwarm.Utils.Teams;
using MuxSwarm.Utils.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// M6 (g12.33) Giga mode: the dynamic-orchestration toggle. Covers command registration, the
/// capability-reference preamble, and the ephemeral-team registry reset semantics. (The tool
/// methods themselves invoke the live model path, so they are exercised manually / via the demo.)
/// </summary>
public class GigaModeTests
{
    [Fact]
    public void Giga_IsRegistered_InCommandCatalogAndHelp()
    {
        Assert.Contains(TuiCommands.All, e => e.Cmd == "/giga" && e.Scope == TuiCommands.Scope.ReplOnly);
        Assert.Contains("/giga", MuxSwarm.Utils.Help.HelpText);
    }

    [Fact]
    public void Preamble_DescribesTheOrchestrationTools()
    {
        var p = GigaMode.Preamble();
        Assert.Contains("Giga Mode", p);
        Assert.Contains("spawn_team", p);
        Assert.Contains("run_team", p);
        Assert.Contains("write_workflow", p);
        Assert.Contains("run_workflow", p);
    }

    [Fact]
    public void Reset_ClearsEphemeralTeams()
    {
        // Reset is idempotent and leaves the registry empty (no ephemeral teams to start).
        GigaMode.Reset();
        Assert.Empty(GigaMode.EphemeralTeams());
    }
}
