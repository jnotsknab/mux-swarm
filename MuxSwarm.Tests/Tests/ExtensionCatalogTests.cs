using MuxSwarm.Engine;
using MuxSwarm.Engine.Extensions;
using MuxSwarm.Engine.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// /extensions: disabled extensions, structured conflicts, the catalog behind the listing, and dispatch.
[Collection("ConsoleState")]
public sealed class ExtensionCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mux-extcatalog-" + Guid.NewGuid().ToString("N"));

    public ExtensionCatalogTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // No ExtensionHost.StopAll(): nothing here starts a process, and stopping the global host would
        // kill a process another (parallel) test class owns.
        ExtensionRegistry.Load(null);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Add(string name, string manifest)
    {
        Directory.CreateDirectory(Path.Combine(_root, name));
        File.WriteAllText(Path.Combine(_root, name, ExtensionRegistry.ManifestFileName), manifest);
    }

    [Fact]
    public void Disabled_IsListedButContributesNothing_AndUnblocksTheLoser()
    {
        Add("demo", """{ "footer": { "template": "{model}" }, "quips": ["D"] }""");
        Add("glow", """{ "footer": { "template": "{effort}" }, "quips": ["G"] }""");

        var both = ExtensionRegistry.Scan([_root]);
        Assert.Equal(["effort"], both.FooterTemplate);
        Assert.Contains(new ExtensionRegistry.Conflict("footer template", "glow", "demo"), both.Conflicts);
        Assert.Contains(new ExtensionRegistry.Conflict("quips", "glow", "demo"), both.Conflicts);
        Assert.DoesNotContain(both.Problems, p => p.Contains("replaces"));   // conflicts are not problems any more

        var one = ExtensionRegistry.Scan([_root], ["glow"]);
        Assert.Equal(["model"], one.FooterTemplate);
        Assert.Equal(["D"], one.Cosmetics.Quips!);
        Assert.Empty(one.Conflicts);
        Assert.Equal("demo", Assert.Single(one.Extensions).Name);
        Assert.Equal("glow", Assert.Single(one.Disabled).Name);
    }

    [Fact]
    public void Load_ReadsDisabledFromConfig()
    {
        Add("a", """{ "themes": [ { "name": "a-theme" } ] }""");
        var result = ExtensionRegistry.Load(new ExtensionsConfig { Enabled = true, Dirs = [_root], Disabled = ["a"] });
        Assert.Empty(result.Themes);
        Assert.Null(Theme.Find("a-theme"));
        Assert.Single(result.Disabled);
    }

    [Fact]
    public void Catalog_BuildsItems_WithOwnProblemsAndConflicts()
    {
        Add("aaa", """{ "spinner": ["a"], "process": { "command": "nope" }, "tools": [ { "name": "x", "description": "d" } ] }""");
        Add("bbb", """{ "spinner": ["b"] }""");
        Add("ccc", """{ "description": "off" }""");
        var cfg = new ExtensionsConfig { Enabled = true, Dirs = [_root], Disabled = ["ccc"] };
        var loaded = ExtensionRegistry.Scan([_root], cfg.Disabled);

        var items = ExtensionCatalog.Build(loaded, cfg, ["aaa: process not started; add \"aaa\" to extensions.trusted to allow it.", "general warning"], [], out var other);

        Assert.Equal(["aaa", "bbb", "ccc"], items.Select(i => i.Name));
        var a = items[0];
        Assert.True(a.Enabled);
        Assert.True(a.HasProcess);
        Assert.Equal("not trusted", a.Process);
        Assert.Contains("tools: x", a.Contributes);
        Assert.Equal(["process not started; add \"aaa\" to extensions.trusted to allow it."], a.Problems);
        Assert.Equal(new ExtensionRegistry.Conflict("spinner", "bbb", "aaa"), Assert.Single(a.Conflicts));
        Assert.Single(items[1].Conflicts);
        Assert.False(items[2].Enabled);
        Assert.Equal(["general warning"], other);
    }

    [Fact]
    public void Catalog_SetEnabledAndTrusted_EditTheConfigLists()
    {
        var cfg = new ExtensionsConfig();
        Assert.True(ExtensionCatalog.SetEnabled(cfg, "glow", false));
        Assert.False(ExtensionCatalog.SetEnabled(cfg, "glow", false));
        Assert.Equal(["glow"], cfg.Disabled);
        Assert.True(ExtensionCatalog.SetEnabled(cfg, "glow", true));
        Assert.Empty(cfg.Disabled);
        Assert.True(ExtensionCatalog.SetTrusted(cfg, "demo", true));
        Assert.Equal(["demo"], cfg.Trusted);
        Assert.True(ExtensionCatalog.SetTrusted(cfg, "demo", false));
        Assert.Empty(cfg.Trusted);
    }

    // ---- dispatch -------------------------------------------------------------------------

    [Fact]
    public async Task Extensions_RunsInsideASession_InsteadOfOfferingToEndIt()
    {
        var entry = Assert.Single(TuiCommands.All, e => e.Cmd == "/extensions");
        Assert.Equal(TuiCommands.Scope.Both, entry.Scope);
        Assert.True(TuiCommands.IsSessionNative("/extensions"));

        // A "y" on stdin would accept the old "only runs at the main menu. End this session?" offer.
        bool stdio = MuxConsole.StdioMode;
        var input = MuxConsole.InputOverride;
        try
        {
            MuxConsole.StdioMode = true;
            MuxConsole.InputOverride = new StringReader("y\n");
            var result = await MetaCommandDispatch.TryHandleAsync("/extensions bogus");
            Assert.Equal(MetaCommandDispatch.Result.Handled, result);
            Assert.Null(SingleAgentOrchestrator.PendingReplCommand);
        }
        finally
        {
            MuxConsole.StdioMode = stdio;
            MuxConsole.InputOverride = input;
            SingleAgentOrchestrator.PendingReplCommand = null;
        }

        var single = File.ReadAllText(Path.Combine(SourceRoot(), "Engine", "SingleAgentOrchestrator.cs"));
        int ext = single.IndexOf("CliCmdUtils.HandleExtensions(metaCmd", StringComparison.Ordinal);
        Assert.True(ext >= 0 && ext < single.IndexOf("Tui.TuiCommands.IsReplOnly(metaCmd.Split(' ', 2)[0])", StringComparison.Ordinal));
    }

    private static string SourceRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));
}
