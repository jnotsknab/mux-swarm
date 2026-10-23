using System.Security.Cryptography;
using System.Text;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>The splash at every breakpoint and the full-screen pickers keep the stock look with no styles
/// set. Hashes recorded on the pre-change code (1ca7758) from this same script.</summary>
[Collection("ConsoleState")]
public class TuiSplashChromeDefaultLookTests
{
    internal static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.Replace("\r\n", "\n"))))[..16];

    internal static string RenderSplash()
    {
        var prior = Theme.Active;
        var sb = new StringBuilder();
        try
        {
            Theme.Set(Theme.Default);
            var recent = new List<MuxConsole.SplashSession>
            {
                new("2026-10-20_17-52-09", "agent", "fix the login bug"),
                new("2026-10-19_09-00-00", "swarm", "write the release notes for the nightly build"),
            };
            foreach (int w in new[] { 40, 120, 200, 260 })
                foreach (var l in MuxConsole.BuildFrameSplashLines("1.2.3", "", "Tip", "hello there", w, recent))
                    sb.Append(TuiMarkup.ToAnsi(l)).Append('\n');
        }
        finally { Theme.Set(prior); }
        return sb.ToString();
    }

    [Fact]
    public void Splash_NoStyles_IsUnchanged() => Assert.Equal("3ACA2AE68D0DDACA", Hash(RenderSplash()));
}
