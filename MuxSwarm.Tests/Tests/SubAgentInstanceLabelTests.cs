using System.Collections.Generic;
using MuxSwarm.Utils;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Sub-agent instance-label allocation: when the SAME agent is delegated multiple times
/// concurrently (delegate_parallel with duplicate AgentNames, e.g. CompanionAgent x3), each live
/// instance must get a DISTINCT display label so the web app roster (keyed by agent name) renders
/// separate rows and the lead's own frames (same base name) are never misrouted into the roster.
/// The first live instance keeps the bare name (byte-identical to the common single-delegation
/// case); concurrent collisions get " 2", " 3", ...; labels are recycled after release.
/// </summary>
public class SubAgentInstanceLabelTests
{
    [Fact]
    public void FirstInstance_KeepsBareName()
    {
        var a = ParallelSwarmOrchestrator.AcquireInstanceLabel("CompanionAgent");
        try { Assert.Equal("CompanionAgent", a); }
        finally { ParallelSwarmOrchestrator.ReleaseInstanceLabel(a); }
    }

    [Fact]
    public void ConcurrentDuplicates_GetDistinctSuffixedLabels()
    {
        var a = ParallelSwarmOrchestrator.AcquireInstanceLabel("CompanionAgent");
        var b = ParallelSwarmOrchestrator.AcquireInstanceLabel("CompanionAgent");
        var c = ParallelSwarmOrchestrator.AcquireInstanceLabel("CompanionAgent");
        try
        {
            Assert.Equal("CompanionAgent", a);
            Assert.Equal("CompanionAgent 2", b);
            Assert.Equal("CompanionAgent 3", c);
            Assert.Equal(3, new HashSet<string> { a, b, c }.Count); // all distinct
        }
        finally
        {
            ParallelSwarmOrchestrator.ReleaseInstanceLabel(a);
            ParallelSwarmOrchestrator.ReleaseInstanceLabel(b);
            ParallelSwarmOrchestrator.ReleaseInstanceLabel(c);
        }
    }

    [Fact]
    public void ReleasedLabel_IsRecycled()
    {
        var a = ParallelSwarmOrchestrator.AcquireInstanceLabel("WebAgent");
        var b = ParallelSwarmOrchestrator.AcquireInstanceLabel("WebAgent"); // "WebAgent 2"
        ParallelSwarmOrchestrator.ReleaseInstanceLabel(a);                  // free the bare name
        var c = ParallelSwarmOrchestrator.AcquireInstanceLabel("WebAgent"); // reuses bare name
        try
        {
            Assert.Equal("WebAgent", a);
            Assert.Equal("WebAgent 2", b);
            Assert.Equal("WebAgent", c);
        }
        finally
        {
            ParallelSwarmOrchestrator.ReleaseInstanceLabel(b);
            ParallelSwarmOrchestrator.ReleaseInstanceLabel(c);
        }
    }

    [Fact]
    public void NoCollisionWhenBareFreedWhileSuffixLive()
    {
        // The lowest-free-slot rule: if the bare name is freed while " 2" is still live, a new
        // instance must take the bare name again (not re-pick " 2" and collide).
        var a = ParallelSwarmOrchestrator.AcquireInstanceLabel("Agent");   // "Agent"
        var b = ParallelSwarmOrchestrator.AcquireInstanceLabel("Agent");   // "Agent 2"
        ParallelSwarmOrchestrator.ReleaseInstanceLabel(a);                 // free "Agent"
        var c = ParallelSwarmOrchestrator.AcquireInstanceLabel("Agent");   // "Agent" again
        var d = ParallelSwarmOrchestrator.AcquireInstanceLabel("Agent");   // must NOT be "Agent 2"
        try
        {
            Assert.Equal("Agent", c);
            Assert.NotEqual(b, d);
            Assert.Equal("Agent 3", d);
        }
        finally
        {
            ParallelSwarmOrchestrator.ReleaseInstanceLabel(b);
            ParallelSwarmOrchestrator.ReleaseInstanceLabel(c);
            ParallelSwarmOrchestrator.ReleaseInstanceLabel(d);
        }
    }

    [Fact]
    public void DistinctBaseNames_Independent()
    {
        var a = ParallelSwarmOrchestrator.AcquireInstanceLabel("CodeAgent");
        var b = ParallelSwarmOrchestrator.AcquireInstanceLabel("WebAgent");
        try { Assert.Equal("CodeAgent", a); Assert.Equal("WebAgent", b); }
        finally { ParallelSwarmOrchestrator.ReleaseInstanceLabel(a); ParallelSwarmOrchestrator.ReleaseInstanceLabel(b); }
    }
}
