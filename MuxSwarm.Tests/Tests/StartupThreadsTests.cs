using System.Threading;
using MuxSwarm.Engine;
using Xunit;

namespace MuxSwarm.Tests.Tests;

public class StartupThreadsTests
{
    [Fact]
    public void Apply_RaisesTheFloor_AndNeverLowersIt()
    {
        ThreadPool.GetMinThreads(out int w0, out int i0);
        try
        {
            var (w, i) = StartupThreads.Apply();
            Assert.True(w >= StartupThreads.Floor && i >= StartupThreads.Floor);
            Assert.True(w >= w0 && i >= i0);

            // A higher minimum set by someone else stays.
            ThreadPool.SetMinThreads(StartupThreads.Floor + 8, StartupThreads.Floor + 8);
            var (w2, i2) = StartupThreads.Apply();
            Assert.Equal(StartupThreads.Floor + 8, w2);
            Assert.Equal(StartupThreads.Floor + 8, i2);
        }
        finally { ThreadPool.SetMinThreads(w0, i0); }
    }
}
