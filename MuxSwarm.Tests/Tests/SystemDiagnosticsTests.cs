using MuxSwarm.Utils;

namespace MuxSwarm.Tests.Tests;

// BuildSnapshot reads App.Config + global runtime statics; serialize with other config-state tests.
[Collection("ExecLimitsState")]
public class SystemDiagnosticsTests
{
    [Fact]
    public void BuildSnapshot_IncludesCoreSections_AndVersion()
    {
        var snap = SystemDiagnostics.BuildSnapshot();
        Assert.Contains("Mux-Swarm runtime snapshot", snap);
        Assert.Contains("version:", snap);
        Assert.Contains("## Provider", snap);
        Assert.Contains("## MCP servers", snap);
        Assert.Contains("## Skills", snap);
        Assert.Contains("## Execution sandbox", snap);
        Assert.Contains("## Filesystem & limits", snap);
    }

    [Fact]
    public void BuildSnapshot_ReportsActivityTimeout()
    {
        var prev = ExecutionLimits.Current;
        try
        {
            ExecutionLimits.Current = new ExecutionLimits { ActivityTimeoutSeconds = 1234 };
            var snap = SystemDiagnostics.BuildSnapshot();
            Assert.Contains("activityTimeoutSeconds: 1234", snap);
        }
        finally { ExecutionLimits.Current = prev; }
    }
}
