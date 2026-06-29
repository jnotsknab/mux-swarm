using System.Threading;
using System.Threading.Tasks;
using MuxSwarm.Utils;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// g12.29 live-session /detach: the cooperative frame-parking handshake. A detached interactive
/// session parks its async frame by awaiting the attach gate (preserving its whole closure) while
/// the menu reclaims the single console reader; /attach releases the gate. These verify the
/// registry handshake headlessly (no console), including the single-permit park->attach rendezvous
/// and id lookup ergonomics.
/// </summary>
public class InteractiveSessionTests
{
    [Fact]
    public void Create_AssignsSequentialIds_AndStartsActive()
    {
        var a = InteractiveSessionRegistry.Create("agent", "MuxAgent");
        var b = InteractiveSessionRegistry.Create("stateless", "stateless");
        Assert.StartsWith("sess", a.Id);
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal("active", a.Status);
        InteractiveSessionRegistry.Remove(a);
        InteractiveSessionRegistry.Remove(b);
    }

    [Fact]
    public void Find_AcceptsBareNumberAndSessPrefix()
    {
        var s = InteractiveSessionRegistry.Create("agent", "MuxAgent");
        var num = s.Id.Substring("sess".Length);
        Assert.Same(s, InteractiveSessionRegistry.Find(s.Id));
        Assert.Same(s, InteractiveSessionRegistry.Find(num));
        Assert.Null(InteractiveSessionRegistry.Find("nope"));
        InteractiveSessionRegistry.Remove(s);
    }

    [Fact]
    public async Task ParkThenAttach_Rendezvous_ResumesFrame()
    {
        var s = InteractiveSessionRegistry.Create("agent", "MuxAgent");

        // Simulate the parked frame: announce park + await the attach gate.
        var frame = Task.Run(async () =>
            await s.ParkAndAwaitAttachAsync(CancellationToken.None));

        // The menu observes the detach signal (frame parked), then it is listed as parked.
        await s.WaitForDetachAsync();
        Assert.Equal("parked", s.Status);
        Assert.Contains(InteractiveSessionRegistry.ListParked(), p => p.Id == s.Id);

        // /attach releases the gate -> the frame resumes and goes active again.
        s.ReleaseAttach();
        await frame.WaitAsync(System.TimeSpan.FromSeconds(5));
        Assert.Equal("active", s.Status);
        Assert.DoesNotContain(InteractiveSessionRegistry.ListParked(), p => p.Id == s.Id);

        InteractiveSessionRegistry.Remove(s);
    }

    [Fact]
    public void ListParked_ExcludesActiveSessions()
    {
        var s = InteractiveSessionRegistry.Create("agent", "MuxAgent");
        // Active by default -> not parked.
        Assert.DoesNotContain(InteractiveSessionRegistry.ListParked(), p => p.Id == s.Id);
        InteractiveSessionRegistry.Remove(s);
    }
}
