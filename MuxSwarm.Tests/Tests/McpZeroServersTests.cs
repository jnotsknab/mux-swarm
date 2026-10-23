using MuxSwarm.Engine;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>Non-strict MCP readiness: a setup with no enabled MCP servers (native tools only) is ready, not failed.</summary>
[Collection("ConsoleState")]
public class McpZeroServersTests
{
    [Fact]
    public async Task NoEnabledServers_IsReady_InNonStrictMode()
    {
        var config = new AppConfig { McpServers = new() { ["Off"] = new McpServerConfig { Enabled = false, Command = "nope" } } };
        Assert.True(await App.InitMcpServersAsync(config));
    }
}
