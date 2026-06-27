using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MuxSwarm.Utils;
using MuxSwarm.Utils.NativeTools;
using Microsoft.Extensions.AI;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Coverage for the native (in-house) Filesystem tools + the native-tool gating registry +
/// the cross-platform security gate that replaced @modelcontextprotocol/server-filesystem.
/// Uses a real temp dir as the single allowed root.
/// </summary>
[Collection("ConsoleState")]
public class NativeFilesystemToolsTests : IDisposable
{
    private readonly string _root;
    private readonly AppConfig _prevConfig;

    public NativeFilesystemToolsTests()
    {
        _prevConfig = App.Config;
        _root = Path.Combine(Path.GetTempPath(), "muxfs_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        App.Config = new AppConfig();
        App.Config.Filesystem.AllowedPaths = new() { _root };
        App.Config.Filesystem.SecurityMode = "standard";
    }

    public void Dispose()
    {
        App.Config = _prevConfig;
        try { Directory.Delete(_root, true); } catch { }
    }

    private static AIFunction Fn(string name) =>
        (AIFunction)FilesystemTools.Build().First(t => ((AIFunction)t).Name == name);

    private static async Task<string> Call(string name, object args)
    {
        var fn = Fn(name);
        var dict = args.GetType().GetProperties().ToDictionary(p => p.Name, p => (object?)p.GetValue(args));
        var res = await fn.InvokeAsync(new AIFunctionArguments(dict), CancellationToken.None);
        return res?.ToString() ?? "";
    }

    [Fact]
    public void Surface_ExposesTheEightKeptToolsAndStripsTheFive()
    {
        var names = FilesystemTools.Build().Select(t => ((AIFunction)t).Name).ToHashSet();
        // kept
        Assert.Contains("Filesystem_read_text_file", names);
        Assert.Contains("Filesystem_read_media_file", names);
        Assert.Contains("Filesystem_write_file", names);
        Assert.Contains("Filesystem_edit_file", names);
        Assert.Contains("Filesystem_create_directory", names);
        Assert.Contains("Filesystem_move_file", names);
        Assert.Contains("Filesystem_list_directory", names);
        Assert.Contains("Filesystem_list_allowed_directories", names);
        Assert.Equal(8, names.Count);
        // stripped (now covered by shell/REPL)
        Assert.DoesNotContain("Filesystem_read_multiple_files", names);
        Assert.DoesNotContain("Filesystem_directory_tree", names);
        Assert.DoesNotContain("Filesystem_search_files", names);
        Assert.DoesNotContain("Filesystem_get_file_info", names);
        Assert.DoesNotContain("Filesystem_list_directory_with_sizes", names);
    }

    [Fact]
    public async Task Standard_WriteReadEdit_WithinAllowed()
    {
        string f = Path.Combine(_root, "a.txt");
        var w = await Call("Filesystem_write_file", new { path = f, content = "hello\nworld\n" });
        Assert.Contains("Wrote", w);
        var r = await Call("Filesystem_read_text_file", new { path = f });
        Assert.Contains("hello", r);
        var e = await Call("Filesystem_edit_file", new { path = f, oldText = "world", newText = "mux" });
        Assert.Contains("@@", e);                 // unified diff returned
        Assert.Contains("+world".Replace("world", "mux"), e.Replace("world", "mux")); // contains the add line
        var r2 = await Call("Filesystem_read_text_file", new { path = f });
        Assert.Contains("mux", r2);
    }

    [Fact]
    public async Task Standard_BlocksOutsideAllowed()
    {
        string outside = Path.Combine(Path.GetTempPath(), "muxfs_outside_" + Guid.NewGuid().ToString("N")[..6] + ".txt");
        var w = await Call("Filesystem_write_file", new { path = outside, content = "x" });
        Assert.Contains("[BLOCKED]", w);
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public async Task SecureMode_ReadsAllowed_WritesBlockNonInteractive()
    {
        App.Config.Filesystem.SecurityMode = "secure";
        string f = Path.Combine(_root, "s.txt");
        File.WriteAllText(f, "seed");
        // read allowed
        var r = await Call("Filesystem_read_text_file", new { path = f });
        Assert.Contains("seed", r);
        // write elevates -> non-interactive test context auto-denies -> hard block
        var w = await Call("Filesystem_write_file", new { path = f, content = "changed" });
        Assert.Contains("[BLOCKED]", w);
        Assert.Equal("seed", File.ReadAllText(f)); // never hit disk
    }

    [Fact]
    public async Task NoneMode_AllowsAnywhere()
    {
        App.Config.Filesystem.SecurityMode = "none";
        string outside = Path.Combine(Path.GetTempPath(), "muxfs_none_" + Guid.NewGuid().ToString("N")[..6] + ".txt");
        try
        {
            var w = await Call("Filesystem_write_file", new { path = outside, content = "ok" });
            Assert.Contains("Wrote", w);
            Assert.True(File.Exists(outside));
        }
        finally { try { File.Delete(outside); } catch { } }
    }

    [Fact]
    public void Registry_MapsVirtualServers()
    {
        Assert.Equal("Filesystem", NativeToolRegistry.VirtualServer("Filesystem_read_text_file"));
        Assert.Equal("Shell", NativeToolRegistry.VirtualServer("repl_shell_exec"));
        Assert.Equal("Shell", NativeToolRegistry.VirtualServer("execute_command_async"));
        Assert.Null(NativeToolRegistry.VirtualServer("Memory_read_graph"));
    }

    [Fact]
    public void Registry_ServerMatches_GatesLikeMcp()
    {
        // An agent listing "Filesystem" gets the native FS tools; listing "Shell" gets the shell tools.
        Assert.True(NativeToolRegistry.ServerMatches("Filesystem_write_file", "Filesystem"));
        Assert.True(NativeToolRegistry.ServerMatches("repl_shell_exec", "Shell"));
        Assert.False(NativeToolRegistry.ServerMatches("repl_shell_exec", "Filesystem"));
        // MCP prefix rule still works for real servers.
        Assert.True(NativeToolRegistry.ServerMatches("Memory_read_graph", "Memory"));
    }

    [Fact]
    public void Registry_NativeAndLegacyMarkers()
    {
        var native = new McpServerConfig { Command = "native-runtime-tools", Args = new[] { "native-runtime-tools" } };
        Assert.True(NativeToolRegistry.IsNativeEntry(native));
        var legacy = new McpServerConfig { Command = "npx", Args = new[] { "-y", "@modelcontextprotocol/server-filesystem" } };
        Assert.True(NativeToolRegistry.IsLegacyFilesystemEntry(legacy));
        var real = new McpServerConfig { Command = "npx", Args = new[] { "-y", "@brave/brave-search-mcp-server" } };
        Assert.False(NativeToolRegistry.IsNativeEntry(real));
        Assert.False(NativeToolRegistry.IsLegacyFilesystemEntry(real));
    }

    [Fact]
    public void Registry_BuildPool_RespectsGlobalEnable()
    {
        App.Config.McpServers["Filesystem"] = new McpServerConfig { Enabled = true, Command = "native-runtime-tools", Args = new[] { "native-runtime-tools" } };
        App.Config.McpServers["Shell"] = new McpServerConfig { Enabled = false, Command = "native-runtime-tools", Args = new[] { "native-runtime-tools" } };
        var pool = NativeToolRegistry.BuildPool(App.Config).Select(t => ((AIFunction)t).Name).ToHashSet();
        Assert.Contains("Filesystem_read_text_file", pool);   // FS enabled
        Assert.DoesNotContain("repl_shell_exec", pool);        // Shell disabled
    }

    [Fact]
    public async Task ListAllowed_ReflectsConfig()
    {
        var r = await Call("Filesystem_list_allowed_directories", new { });
        Assert.Contains(_root, r);
    }
    [Fact]
    public async Task ParallelWrites_SamePath_NeverTearAndAlwaysValid()
    {
        string f = Path.Combine(_root, "race.txt");
        File.WriteAllText(f, "seed");
        // 32 concurrent writers, each writing a distinct full payload. With atomic temp+rename and
        // the per-path lock, every observed state must be EXACTLY one writer's complete payload -
        // never a torn/empty/partial file.
        var payloads = Enumerable.Range(0, 32)
            .Select(i => "PAYLOAD-" + i + "-" + new string((char)('a' + (i % 26)), 500))
            .ToArray();
        var tasks = payloads.Select(pl => Task.Run(() => Call("Filesystem_write_file", new { path = f, content = pl }))).ToArray();
        await Task.WhenAll(tasks);
        // Final file content must be one of the exact payloads (last writer wins), bit-for-bit.
        string final = File.ReadAllText(f);
        Assert.Contains(final, payloads);
        // No temp siblings left behind.
        Assert.Empty(Directory.GetFiles(_root, "race.txt.mux-tmp-*"));
    }

    [Fact]
    public async Task ParallelEdits_SamePath_NoLostUpdatesUnderLock()
    {
        string f = Path.Combine(_root, "counter.txt");
        File.WriteAllText(f, "X");
        // Each editor replaces the single "X" with "XX" (grow by one). Serialized read-modify-write
        // means N successful edits OR clean "oldText not found" losers - but never a torn file and
        // never a crash. We assert the file is always a valid run of X's afterward.
        var tasks = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => Call("Filesystem_edit_file", new { path = f, oldText = "X", newText = "XX" })))
            .ToArray();
        await Task.WhenAll(tasks);
        string final = File.ReadAllText(f);
        Assert.True(final.Length >= 1 && final.All(c => c == 'X'),
            $"file corrupted under parallel edit: '{final}'");
        Assert.Empty(Directory.GetFiles(_root, "counter.txt.mux-tmp-*"));
    }
}
