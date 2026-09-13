using MuxSwarm.Engine;
using MuxSwarm.Engine.NativeTools;

namespace MuxSwarm.Tests.Tests;

/// <summary>Native tool configuration is independent of MCP transport and per-agent access.</summary>
[Collection("ConsoleState")]
public class NativeToolDiagnosticsTests
{
    [Theory]
    [InlineData("Filesystem", "npx", "@modelcontextprotocol/server-filesystem")]
    [InlineData("Shell", "uvx", "mcp-async-repl")]
    [InlineData("LegacyAlias", "C:/tools/MCP-ASYNC-REPL.exe", "")]
    [InlineData("Marker", "prefix-native-runtime-tools-suffix", "")]
    public void Snapshot_DoesNotFlagIntentionallyReplacedServers(string name, string command, string argument)
    {
        var previous = App.Config;
        try
        {
            App.Config = new AppConfig
            {
                McpServers = new() { [name] = new McpServerConfig { Command = command, Args = new[] { argument } } }
            };
            string snapshot = SystemDiagnostics.BuildSnapshot();
            Assert.DoesNotContain("NOT CONNECTED", snapshot);
            Assert.Contains("native", snapshot);
        }
        finally { App.Config = previous; }
    }

    [Fact]
    public void Snapshot_DisabledNativeIsNotReportedEnabled()
    {
        var previous = App.Config;
        try
        {
            App.Config = new AppConfig
            {
                McpServers = new() { ["Shell"] = new McpServerConfig { Command = NativeToolRegistry.NativeMarker, Enabled = false } }
            };
            string snapshot = SystemDiagnostics.BuildSnapshot();
            Assert.Contains("Shell: disabled", snapshot);
            Assert.DoesNotContain("NOT CONNECTED", snapshot);
        }
        finally { App.Config = previous; }
    }

    [Fact]
    public void Snapshot_MissingConfigStillReportsNativeDefaultsAndAccessBoundary()
    {
        var previous = App.Config;
        try
        {
            App.Config = new AppConfig();
            string snapshot = SystemDiagnostics.BuildSnapshot();
            Assert.Contains("## Native tool groups", snapshot);
            Assert.Contains("Filesystem: enabled", snapshot);
            Assert.Contains("Shell: enabled", snapshot);
            Assert.Contains("per-agent", snapshot);
        }
        finally { App.Config = previous; }
    }

    [Theory]
    [InlineData("Shell", "stdio", "custom-shell-server", null)]
    [InlineData("Filesystem", "stdio", "custom-files-server", null)]
    [InlineData("ExternalShell", "http", null, "https://tools.example/mcp")]
    [InlineData("ExternalFiles", "stdio", "node", null)]
    public void GenuineExternalEntries_StillRequireConnections(string name, string type, string? command, string? url)
    {
        var configured = new Dictionary<string, McpServerConfig>
        {
            [name] = new() { Type = type, Command = command, Url = url }
        };
        var missing = Assert.Single(SystemDiagnostics.ClassifyServers(configured, Array.Empty<string>()));
        Assert.True(missing.MissingConnection);
        Assert.Contains("NOT CONNECTED", missing.State);
        var connected = Assert.Single(SystemDiagnostics.ClassifyServers(configured, new[] { name }));
        Assert.False(connected.MissingConnection);
        Assert.StartsWith("connected", connected.State);
        configured[name].Enabled = false;
        var disabled = Assert.Single(SystemDiagnostics.ClassifyServers(configured, Array.Empty<string>()));
        Assert.False(disabled.MissingConnection);
        Assert.StartsWith("disabled", disabled.State);
    }

    [Fact]
    public void ExtractedPredicate_MatchesOriginalStartupPolicyAcrossCommandArgumentAndTypeCases()
    {
        static bool Original(McpServerConfig c)
        {
            bool Has(string? s) => s is not null && s.Contains("mcp-async-repl", StringComparison.OrdinalIgnoreCase);
            return Has(c.Command) || (c.Args?.Any(Has) ?? false)
                || NativeToolRegistry.IsNativeEntry(c) || NativeToolRegistry.IsLegacyFilesystemEntry(c);
        }
        string?[] values = [null, "", "custom-server", "NATIVE-RUNTIME-TOOLS", "prefix-native-runtime-tools-suffix",
            "@modelcontextprotocol/server-filesystem", "C:/tools/MCP-ASYNC-REPL.exe"];
        foreach (string? command in values)
        foreach (string? argument in values)
        foreach (string type in new[] { "stdio", "http" })
        {
            var config = new McpServerConfig { Type = type, Command = command, Args = argument is null ? null : new[] { argument } };
            Assert.Equal(Original(config), NativeToolRegistry.ReplacesMcpEntry(config));
        }
        Assert.False(NativeToolRegistry.ReplacesMcpEntry(null));
    }

    [Fact]
    public void ReplacementAliases_DoNotGrantAgentAccessOrBypassGlobalDisable()
    {
        var config = new AppConfig
        {
            McpServers = new()
            {
                ["Shell"] = new McpServerConfig { Enabled = false, Command = NativeToolRegistry.NativeMarker },
                ["Filesystem"] = new McpServerConfig { Enabled = false, Command = NativeToolRegistry.NativeMarker },
                ["LegacyAlias"] = new McpServerConfig { Command = "mcp-async-repl" },
            }
        };
        var alias = SystemDiagnostics.ClassifyServers(config.McpServers, Array.Empty<string>()).Single(s => s.Name == "LegacyAlias");
        Assert.False(alias.MissingConnection);
        Assert.Contains("canonical groups", alias.State);
        Assert.Empty(NativeToolRegistry.BuildPool(config));
        config.McpServers["Filesystem"].Enabled = true;
        var pool = NativeToolRegistry.BuildPool(config);
        Assert.NotEmpty(pool);
        Assert.Empty(Common.ApplyToolFilter(pool, Array.Empty<string>(), Array.Empty<string>()));
        Assert.Empty(Common.ApplyToolFilter(pool, new[] { "LegacyAlias" }, Array.Empty<string>()));
        Assert.NotEmpty(Common.ApplyToolFilter(pool, new[] { "Filesystem" }, Array.Empty<string>()));
    }

    [Fact]
    public void DoctorHealth_UsesSameNativeClassificationAndKeepsExternalFailure()
    {
        var config = App.Config;
        var provider = App.ActiveProvider;
        bool stdio = MuxConsole.StdioMode;
        var output = Console.Out;
        using var capture = new StringWriter();
        const string external = "DoctorTestExternalMissing";
        Assert.False(App.McpClients.ContainsKey(external));
        try
        {
            App.Config = new AppConfig
            {
                McpServers = new()
                {
                    ["Filesystem"] = new() { Command = "npx", Args = new[] { "@modelcontextprotocol/server-filesystem" } },
                    ["Shell"] = new() { Command = "uvx", Args = new[] { "mcp-async-repl" } },
                    [external] = new() { Command = "real-external-server" },
                }
            };
            App.ActiveProvider = new ProviderConfig { Name = "test-provider", Endpoint = "https://provider.example/v1" };
            MuxConsole.StdioMode = true;
            Console.SetOut(capture);
            typeof(SingleAgentOrchestrator).GetMethod("HandleDoctor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, null);
            var events = capture.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Single(events);
            using var doc = System.Text.Json.JsonDocument.Parse(events[0]);
            string rendered = doc.RootElement.GetProperty("content").GetString()!;
            Assert.Contains(external, rendered);
            Assert.Contains("NOT CONNECTED", rendered);
            Assert.DoesNotContain("MCP 'Shell' NOT CONNECTED", rendered);
            Assert.DoesNotContain("MCP 'Filesystem' NOT CONNECTED", rendered);
            Assert.Contains("Native tool groups", rendered);
        }
        finally
        {
            Console.SetOut(output);
            MuxConsole.StdioMode = stdio;
            App.Config = config;
            App.ActiveProvider = provider;
        }
    }
}
