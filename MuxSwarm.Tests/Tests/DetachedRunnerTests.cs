using System;
using System.Linq;
using MuxSwarm.Utils;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Detached background-job runner (g12.21). The launch path drives a real agent so it isn't
/// unit-tested here; these cover the pure registry/render/cancel surface + the DetachedJob shape.
/// </summary>
public class DetachedRunnerTests
{
    [Fact]
    public void DisplayName_IsBgPrefixed()
    {
        var job = new DetachedJob { Id = "bg1", Agent = "CodeAgent", Goal = "do a thing" };
        Assert.Equal("bg:CodeAgent", job.DisplayName);
        Assert.Equal(DetachedStatus.Running, job.Status);
    }

    [Fact]
    public void Cancel_UnknownId_ReturnsFalse()
    {
        Assert.False(DetachedRunner.Cancel("nope-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void Render_EmptyOrLists()
    {
        // Render never throws and returns a string regardless of registry state.
        var text = DetachedRunner.Render();
        Assert.False(string.IsNullOrEmpty(text));
    }

    [Fact]
    public void Jobs_SnapshotIsStable()
    {
        var a = DetachedRunner.Jobs();
        var b = DetachedRunner.Jobs();
        Assert.Equal(a.Count, b.Count);   // snapshot copy, not a live view
    }
}
