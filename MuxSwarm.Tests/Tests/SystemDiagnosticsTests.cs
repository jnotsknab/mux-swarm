using MuxSwarm.Engine;
using MuxSwarm.Engine.NativeTools;

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

    [Theory]
    [InlineData("host")]
    [InlineData("")]
    [InlineData("none")]
    public void ProbeSandbox_NoBackend_SaysSoPlainly(string backend)
    {
        var s = SystemDiagnostics.ProbeSandbox(new SandboxConfig { Backend = backend });
        Assert.False(s.Configured);
        Assert.True(s.Usable);
        Assert.Contains("no sandbox configured", s.Detail);
    }

    [Theory]
    [InlineData("custom", "sandbox.command")]   // custom without a template: no process spawned on any OS
    [InlineData("nosuch", "Unknown sandbox.backend")]
    public void ProbeSandbox_UnusableBackend_ReportsTheReason(string backend, string reason)
    {
        var s = SystemDiagnostics.ProbeSandbox(new SandboxConfig { Backend = backend, Command = "" });
        Assert.True(s.Configured);
        Assert.False(s.Usable);
        Assert.Contains(reason, s.Detail);
    }

    [Fact]
    public void Snapshot_WithoutProbe_DoesNotClaimSandboxStatus()
    {
        Assert.Contains("status: not probed", SystemDiagnostics.BuildSnapshot());
        var unusable = new SystemDiagnostics.SandboxStatus(true, false, "runsc missing");
        Assert.Contains("status: NOT USABLE -- runsc missing", SystemDiagnostics.BuildSnapshot(unusable));
    }

    [Fact]
    public void RuntimeProblem_ChecksPathAndDockerRegistration()
    {
        string missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mux-no-such-runsc-" + Guid.NewGuid().ToString("N"));
        Assert.Contains("does not exist", SystemDiagnostics.RuntimeProblem("podman", missing, null));
        string existing = System.IO.Path.GetTempFileName();
        try { Assert.Null(SystemDiagnostics.RuntimeProblem("podman", existing, null)); }
        finally { System.IO.File.Delete(existing); }
        Assert.Contains("not registered", SystemDiagnostics.RuntimeProblem("docker", "runsc", " Runtimes: io.containerd.runc.v2 runc"));
        Assert.Null(SystemDiagnostics.RuntimeProblem("docker", "runsc", " Runtimes: runc runsc"));
        Assert.Null(SystemDiagnostics.RuntimeProblem("docker", "runsc", ""));         // info unavailable: not guessed
        Assert.Null(SystemDiagnostics.RuntimeProblem("podman", "runsc", "anything"));  // podman runtimes not listed reliably
    }
}
