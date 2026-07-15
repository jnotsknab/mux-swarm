using System.Reflection;
using MuxSwarm.Utils;
using Xunit;

namespace MuxSwarm.Tests.Tests;

public class ProcessCleanupTests
{
    [Fact]
    public void ProcessCleanup_HasNoPidDerivedProcessGroupSweep()
    {
        const BindingFlags AllMethods =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Static | BindingFlags.Instance;

        var methods = typeof(ProcessCleanup).GetMethods(AllMethods);

        Assert.DoesNotContain(methods, method => method.Name == "KillOwnProcessTree");
    }
}
