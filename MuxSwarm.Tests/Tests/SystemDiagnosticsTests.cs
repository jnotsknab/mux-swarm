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
    public void RuntimeProblem_AbsolutePath_CheckedOnlyOnLinux()
    {
        string missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mux-no-such-runsc-" + Guid.NewGuid().ToString("N"));
        Assert.Contains("does not exist", SystemDiagnostics.RuntimeProblem("podman", missing, null, isLinux: true));
        // macOS/Windows: the engine runs in a VM, so a host-side path check would be a false NOT USABLE.
        Assert.Null(SystemDiagnostics.RuntimeProblem("podman", missing, null, isLinux: false));
        Assert.Null(SystemDiagnostics.RuntimeProblem("docker", missing, null, isLinux: false));
        string existing = System.IO.Path.GetTempFileName();
        try { Assert.Null(SystemDiagnostics.RuntimeProblem("podman", existing, null, isLinux: true)); }
        finally { System.IO.File.Delete(existing); }
    }

    [Theory]
    [InlineData("runsc", "{\"runc\":{\"path\":\"runc\"},\"runsc\":{\"path\":\"/usr/local/bin/runsc\"}}", false)]
    [InlineData("runsc", "{\"io.containerd.runc.v2\":{\"path\":\"runc\"},\"runc\":{\"path\":\"runc\"}}", true)]
    [InlineData("runc", "{\"runc-custom\":{\"path\":\"x\"},\"io.containerd.runc.v2\":{}}", true)]   // no substring match
    [InlineData("RUNSC", "{\"runsc\":{}}", true)]                                                         // exact, ordinal
    [InlineData("runsc", "", false)]                    // info unavailable: not guessed
    [InlineData("runsc", "Runtimes: runc", false)]      // malformed JSON: cannot check
    [InlineData("runsc", "[\"runc\"]", false)]          // not an object: cannot check
    public void RuntimeProblem_DockerName_MatchesExactRuntimeKeys(string runtime, string json, bool problem)
    {
        var r = SystemDiagnostics.RuntimeProblem("docker", runtime, json, isLinux: true);
        if (problem) Assert.Contains("not registered", r);
        else Assert.Null(r);
    }

    [Theory]
    [InlineData("docker", "io.containerd.runsc.v1", false)]   // containerd shims run without registration
    [InlineData("docker", "io.containerd.kata.v2", false)]
    [InlineData("podman", "runsc", false)]                    // podman runtimes not listed reliably
    [InlineData("docker", "/usr/local/bin/runsc", false)]     // paths use the existence check instead
    [InlineData("docker", "runsc", true)]
    public void NeedsRuntimeList_OnlyForDockerRuntimeNames(string binary, string runtime, bool expected)
    {
        Assert.Equal(expected, SystemDiagnostics.NeedsRuntimeList(binary, runtime));
        if (!expected) Assert.Null(SystemDiagnostics.RuntimeProblem(binary, runtime, "{\"runc\":{}}", isLinux: false));
    }

    [Fact]
    public void ProbeSandbox_CustomWithTemplate_IsNotProbed_NotUsable()
    {
        var s = SystemDiagnostics.ProbeSandbox(new SandboxConfig { Backend = "custom", Command = "sh -c {cmd}" });
        Assert.True(s.Configured);
        Assert.False(s.Probed);
        Assert.DoesNotContain("ready", s.Detail);
        string snap = SystemDiagnostics.BuildSnapshot(s);
        Assert.Contains("status: not probed -- ", snap);
        Assert.DoesNotContain("status: usable", snap);
    }
}
