using System;
using System.IO;
using MuxSwarm.Utils;
using MuxSwarm.Utils.NativeTools;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// g12.31 memory-loop hardening: the restated Memory Layers convention + per-agent BRAIN/MEMORY
/// auto-injection. Verifies the index-card / stub discipline text is present and that per-agent
/// escape-valve files are injected ONLY when they exist (file-existence gated = byte-identical when
/// absent). Uses a uniquely-named agent so it never collides with real Context files; cleans up.
/// </summary>
[Collection("ConsoleState")]
public class PreambleMemoryTests
{
    [Fact]
    public void MemoryLayers_StatesPrimacyAndIndexCardRule()
    {
        var p = PreambleBuilder.Build("Orchestrator");
        Assert.Contains("BRAIN.md (PRIMARY", p);
        Assert.Contains("MEMORY.md (PRIMARY", p);
        Assert.Contains("Index-card rule", p);
        Assert.Contains("-> KG:", p);
        Assert.Contains("-> chroma:", p);
    }

    [Fact]
    public void PerAgent_BrainFile_InjectedOnlyWhenPresent()
    {
        var prevMode = AutoInject.Current;
        AutoInject.Current = AutoInject.Mode.Full;
        var agent = "ZZTestAgent_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var file = Path.Combine(PlatformContext.ContextDirectory, $"BRAIN.{agent}.md");
        var marker = "PERAGENT_MARKER_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        try
        {
            // Absent => the per-agent block is not injected for this agent.
            var before = PreambleBuilder.Build(agent);
            Assert.DoesNotContain($"BRAIN.{agent}.md", before);

            Directory.CreateDirectory(PlatformContext.ContextDirectory);
            File.WriteAllText(file, "## Anti-Patterns\n- " + marker + "\n");

            // Present => injected, with the agent-scoped header + content.
            var after = PreambleBuilder.Build(agent);
            Assert.Contains($"[INJECTED CONTEXT FROM BRAIN.{agent}.md]", after);
            Assert.Contains(marker, after);

            // A DIFFERENT agent with no per-agent file does not pick it up.
            var other = PreambleBuilder.Build("Orchestrator");
            Assert.DoesNotContain(marker, other);
        }
        finally
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
            AutoInject.Current = prevMode;
        }
    }

    [Fact]
    public void SandboxBlock_OmittedWhenBackendHost()
    {
        // The bug: the preamble claimed an ACTIVE sandbox + /work + /host/* mounts even when execution
        // runs natively on the host. With backend=host (the default), SandboxRuntime.IsActive is false
        // and the block MUST NOT appear.
        var prev = App.Config.Sandbox;
        try
        {
            App.Config.Sandbox = new SandboxConfig { Backend = "host" };
            SandboxRuntime.Refresh();
            Assert.False(SandboxRuntime.IsActive);
            var p = PreambleBuilder.Build("Orchestrator");
            Assert.DoesNotContain("Execution Sandbox (ACTIVE)", p);
            Assert.DoesNotContain("Your scratch working directory is /work", p);
        }
        finally
        {
            App.Config.Sandbox = prev;
            SandboxRuntime.Refresh();
        }
    }

    [Fact]
    public void SandboxBlock_OmittedWhenBackendEmpty()
    {
        // Empty backend must fall back to host (no sandbox) - the block stays omitted.
        var prev = App.Config.Sandbox;
        try
        {
            App.Config.Sandbox = new SandboxConfig { Backend = "" };
            SandboxRuntime.Refresh();
            Assert.False(SandboxRuntime.IsActive);
            Assert.DoesNotContain("Execution Sandbox (ACTIVE)", PreambleBuilder.Build("Orchestrator"));
        }
        finally
        {
            App.Config.Sandbox = prev;
            SandboxRuntime.Refresh();
        }
    }
}
