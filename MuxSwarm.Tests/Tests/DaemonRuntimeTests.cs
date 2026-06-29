using System.Linq;
using MuxSwarm.State;
using MuxSwarm.Utils;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Runtime daemon control (g12.22): adding/listing/cancelling triggers without a restart. The
/// fire path drives orchestrators so it isn't unit-tested; these cover the registry behavior.
/// </summary>
public class DaemonRuntimeTests
{
    [Fact]
    public void AddTriggerRuntime_BeforeStart_ReturnsNull()
    {
        var runner = new DaemonRunner(new DaemonConfig());
        // Not started (no deps wired) -> cannot add a runtime trigger.
        Assert.Null(runner.AddTriggerRuntime(new DaemonTrigger { Type = "cron", Schedule = "* * * * *", Goal = "x" }));
        Assert.False(runner.IsStarted);
    }

    [Fact]
    public void ListTriggers_IncludesBootTriggers()
    {
        var cfg = new DaemonConfig
        {
            Triggers = { new DaemonTrigger { Id = "boot1", Type = "cron", Schedule = "0 9 * * *", Mode = "agent" } },
        };
        var runner = new DaemonRunner(cfg);
        var list = runner.ListTriggers();
        Assert.Contains(list, t => t.Id == "boot1" && !t.Runtime);
    }

    [Fact]
    public void CancelTrigger_UnknownId_ReturnsFalse()
    {
        var runner = new DaemonRunner(new DaemonConfig());
        Assert.False(runner.CancelTrigger("nope"));
    }
}
