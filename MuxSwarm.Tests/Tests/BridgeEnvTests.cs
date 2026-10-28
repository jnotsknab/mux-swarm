using System.Collections.Specialized;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using MuxSwarm.Engine;
using MuxSwarm.State;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// Daemon bridges must be able to reach /ws out of the box: the URL follows the address serve bound (a tailnet or
// LAN bind does not listen on localhost), the serve.auth token is passed when auth is on, and a reconnecting
// bridge can skip the replay buffer.
public class BridgeEnvTests
{
    [Theory]
    [InlineData("127.0.0.1", "ws://127.0.0.1:6723/ws")]
    [InlineData("100.71.234.84", "ws://100.71.234.84:6723/ws")]   // tailnet bind: localhost would not answer
    [InlineData("0.0.0.0", "ws://127.0.0.1:6723/ws")]
    [InlineData("", "ws://127.0.0.1:6723/ws")]
    [InlineData(null, "ws://127.0.0.1:6723/ws")]
    [InlineData("::", "ws://[::1]:6723/ws")]
    [InlineData("fd7a:115c::1", "ws://[fd7a:115c::1]:6723/ws")]
    public void LocalWsUrl_FollowsTheBoundAddress(string? bind, string expected) =>
        Assert.Equal(expected, ServeMode.LocalWsUrl(bind, 6723));

    [Fact]
    public void BridgeEnv_TailnetBindWithAuth_GetsReachableUrlAndToken()
    {
        var env = new StringDictionary();
        DaemonRunner.BridgeEnvDefaults(env, "100.71.234.84", 6723, new ServeAuthConfig { Enabled = true, Token = "s3cret" });
        Assert.Equal("ws://100.71.234.84:6723/ws", env["MUX_WS_URL"]);
        Assert.Equal("s3cret", env["MUX_SERVE_TOKEN"]);
    }

    [Fact]
    public void BridgeEnv_TokenFromEnvReference_IsResolved()
    {
        const string name = "MUX_TEST_BRIDGE_TOKEN_7F3A";
        Environment.SetEnvironmentVariable(name, "from-env");
        try
        {
            var env = new StringDictionary();
            DaemonRunner.BridgeEnvDefaults(env, "127.0.0.1", 6723, new ServeAuthConfig { Enabled = true, Token = "${" + name + "}" });
            Assert.Equal("from-env", env["MUX_SERVE_TOKEN"]);
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Fact]
    public void BridgeEnv_AuthOff_NoToken()
    {
        var env = new StringDictionary();
        DaemonRunner.BridgeEnvDefaults(env, "127.0.0.1", 6723, new ServeAuthConfig { Enabled = false, Token = "s3cret" });
        Assert.False(env.ContainsKey("MUX_SERVE_TOKEN"));
    }

    [Fact]
    public void BridgeEnv_ExistingValues_AreKept()
    {
        var env = new StringDictionary { ["MUX_WS_URL"] = "ws://custom:1/ws", ["MUX_SERVE_TOKEN"] = "mine" };
        DaemonRunner.BridgeEnvDefaults(env, "100.71.234.84", 6723, new ServeAuthConfig { Enabled = true, Token = "s3cret" });
        Assert.Equal("ws://custom:1/ws", env["MUX_WS_URL"]);
        Assert.Equal("mine", env["MUX_SERVE_TOKEN"]);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    public void Replay_DefaultsOn_ZeroOrFalseSkips(string? value, bool expected)
    {
        var q = value is null
            ? new QueryCollection()
            : new QueryCollection(new Dictionary<string, StringValues> { ["replay"] = value });
        Assert.Equal(expected, ServeMode.WantsReplay(q));
    }
}
