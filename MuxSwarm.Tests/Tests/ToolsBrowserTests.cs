using MuxSwarm.Engine;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

public class ToolsBrowserTests
{
    [Fact]
    public void RankTools_FindsSubsequenceAndSeparatelyTypedTerms()
    {
        var tools = new[] { ("Filesystem_read_text_file", "Read UTF-8 text"), ("GmailMCP_search_emails", "Search mail") };
        Assert.Contains("Filesystem_read_text_file", TuiComponents.RankTools("fsrtf", tools));
        Assert.Contains("Filesystem_read_text_file", TuiComponents.RankTools("filesystem text", tools));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(45)]
    public void ToolsPreview_NeverOverflowsNarrowViewport(int width)
    {
        var tools = new[] { ("Filesystem_read_text_file_with_a_long_name", "A long description that should not spill across the viewport") };
        foreach (string row in TuiComponents.ToolsPreview("", tools, width, 0))
            Assert.InRange(TuiMarkup.MarkupWidth(row), 0, width);
    }

    private sealed class Terminal : ITuiTerminal
    {
        public int Width => 80;
        public int Height => 20;
        public void Write(string text) { }
        public void Flush() { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToolsCompletion_InsertsToolQueryNotBareCommand(bool frame)
    {
        var driver = new TuiDriver(new Terminal(), frameEngine: frame);
        driver.SetToolsCatalog(new[] { ("Filesystem_read_text_file", "read text") });
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var editor = (LineEditor)typeof(TuiDriver).GetField("_editor", flags)!.GetValue(driver)!;
        editor.SetBuffer("/tools read_text");
        typeof(TuiDriver).GetMethod("AcceptCompletion", flags)!.Invoke(driver, null);
        Assert.Equal("/tools Filesystem_read_text_file", editor.Buffer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DriverScopeTransitions_DoNotLeakPreviousScopeTools(bool frame)
    {
        var driver = new TuiDriver(new Terminal(), frameEngine: frame);
        var global = new ToolCatalog.Snapshot("global", new[] { new ToolCatalog.Entry("global_tool", "G", "Server", "MCP") });
        var restricted = new ToolCatalog.Snapshot("agent", new[] { new ToolCatalog.Entry("allowed_tool", "A", "Runtime", "Local") });
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var editor = (LineEditor)typeof(TuiDriver).GetField("_editor", flags)!.GetValue(driver)!;
        var accept = typeof(TuiDriver).GetMethod("AcceptCompletion", flags)!;
        void AssertCompletion(string expected)
        {
            editor.SetBuffer("/tools tool");
            accept.Invoke(driver, null);
            Assert.Equal("/tools " + expected, editor.Buffer);
        }
        driver.SetToolsCatalogProvider(() => global);
        AssertCompletion("global_tool");
        driver.SetToolsCatalog(restricted);
        AssertCompletion("allowed_tool");
        driver.SetToolsCatalogProvider(() => global);
        AssertCompletion("global_tool");
        driver.SetToolsCatalog(restricted); // reattach restores stored scope, not menu catalog
        AssertCompletion("allowed_tool");
    }

    [Theory]
    [InlineData("/tools", "")]
    [InlineData("/tools read text", "read text")]
    [InlineData("  /TOOLS\tread", "read")]
    public void ToolsQueryAndEditorUseSameFilter(string input, string query)
    {
        var editor = new LineEditor();
        editor.SetBuffer(input);
        Assert.True(editor.IsToolsFilter);
        Assert.Equal(query, editor.ToolsFilter);
        Assert.True(ToolCatalog.TryQuery(input, out string parsed));
        Assert.Equal(query, parsed);
    }

    [Fact]
    public void ToolsCommand_IsAvailableInSessionAndAcceptsQuery()
    {
        Assert.True(TuiCommands.IsSessionNative("/tools"));
        Assert.DoesNotContain("ends session", TuiCommands.SessionUnified.Single(e => e.Cmd == "/tools").Desc);
        Assert.True(TuiCommands.TakesArgument("/tools"));
    }

    [Fact]
    public void Catalog_UsesExplicitMcpProvenanceBeforeNativeNameMapping()
    {
        var external = Microsoft.Extensions.AI.AIFunctionFactory.Create(() => "not invoked", "Filesystem_read_text_file", "External read");
        ToolCatalog.RegisterExternal(external, "Server_With_Underscores");
        var local = Microsoft.Extensions.AI.AIFunctionFactory.Create(() => "not invoked", "list_skills", "Available skills");
        var native = MuxSwarm.Engine.NativeTools.NativeToolRegistry.BuildPool(new AppConfig());
        var snapshot = ToolCatalog.FromTools(new Microsoft.Extensions.AI.AITool[] { external, local }.Concat(native), "test");
        Assert.Contains(snapshot.Entries, e => e.Name == external.Name && e.Kind == "MCP" && e.Group == "Server_With_Underscores");
        Assert.Contains(snapshot.Entries, e => e.Name == external.Name && e.Kind == "Native" && e.Group == "Filesystem");
        Assert.Contains(snapshot.Entries, e => e.Name == "repl_shell_exec" && e.Kind == "Native" && e.Group == "Shell");
        Assert.Contains(snapshot.Entries, e => e.Name == "list_skills" && e.Kind == "Local" && e.Group == "Runtime");
    }

    [Fact]
    public void Ranking_UsesGroupDescriptionAndKeepsExactNameFirst()
    {
        var entries = new[]
        {
            new ToolCatalog.Entry("get", "Download resource", "Web_Search", "MCP"),
            new ToolCatalog.Entry("get_more", "Other", "Web_Search", "MCP"),
            new ToolCatalog.Entry("repl_shell_exec", "Run Python", "Shell", "Native"),
        };
        Assert.Equal("get", ToolCatalog.Rank("get", entries)[0].Name);
        Assert.Single(ToolCatalog.Rank("native python", entries));
        Assert.Single(ToolCatalog.Rank("web download", entries));
    }

    [Fact]
    public void Listing_HasDistinctGroupsAndExactQueryRetainsFullDescription()
    {
        string description = new string('x', 150) + " UNIQUE-END";
        var catalog = new ToolCatalog.Snapshot("Agent", new[]
        {
            new ToolCatalog.Entry("Filesystem_read_text_file", description, "Filesystem", "Native"),
            new ToolCatalog.Entry("Web_Search_get", "Web", "Web_Search", "MCP"),
        });
        string listing = string.Join("\n", TuiComponents.ToolsListing("", catalog, 60).Select(TuiMarkup.Plain));
        Assert.Contains("Native · Filesystem (1)", listing);
        Assert.Contains("MCP · Web_Search (1)", listing);
        string detail = string.Join("\n", TuiComponents.ToolsListing("Filesystem_read_text_file", catalog, 60).Select(TuiMarkup.Plain));
        Assert.Contains("UNIQUE-END", detail);
        Assert.DoesNotContain("Web_Search_get", detail);
    }

    [Theory]
    [InlineData(24, 6)]
    [InlineData(80, 12)]
    public void Preview_SelectedLastEntryStaysVisibleAndBounded(int width, int rows)
    {
        var catalog = new ToolCatalog.Snapshot("Agent", Enumerable.Range(0, 50)
            .Select(i => new ToolCatalog.Entry($"tool-{i:00}", "description", "MCP_With_Underscores", "MCP")).ToArray());
        var result = TuiComponents.ToolsPreview("", catalog, width, 49, rows);
        Assert.Contains(result, row => TuiMarkup.Plain(row).Contains("tool-49"));
        Assert.InRange(result.Count, 1, rows);
        Assert.All(result, row => Assert.InRange(TuiMarkup.MarkupWidth(row), 0, width));
    }

    [Fact]
    public void Catalog_ProjectsOnlySuppliedToolsAndDoesNotInvokeThem()
    {
        int invoked = 0;
        var tool = Microsoft.Extensions.AI.AIFunctionFactory.Create(() => { invoked++; return "unused"; }, "only_this", "Allowed");
        var catalog = ToolCatalog.FromTools(new[] { tool }, "limited agent");
        Assert.Single(catalog.Entries);
        TuiComponents.ToolsListing("", catalog, 80);
        Assert.Equal(0, invoked);
        Assert.Empty(ToolCatalog.FromTools(Array.Empty<Microsoft.Extensions.AI.AITool>(), "empty").Entries);
    }
}

[Collection("ConsoleState")]
public class ToolsCommandScopeTests
{
    [Theory]
    [InlineData("swarm")]
    [InlineData("pswarm")]
    [InlineData("agent")]
    public async Task InSessionCommand_RendersOnlyCallerScopeWithoutEndingSessionOrInvoking(string mode)
    {
        bool previous = MuxConsole.StdioMode;
        var stdout = Console.Out;
        var pending = SingleAgentOrchestrator.PendingReplCommand;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            MuxConsole.StdioMode = true;
            var catalog = new ToolCatalog.Snapshot(mode, new[] { new ToolCatalog.Entry("safe_name", "Description", "Runtime", "Local") });
            var result = await MetaCommandDispatch.TryHandleAsync("/tools safe", tools: catalog);
            Assert.Equal(MetaCommandDispatch.Result.Handled, result);
            Assert.Equal(pending, SingleAgentOrchestrator.PendingReplCommand);
            using var doc = System.Text.Json.JsonDocument.Parse(output.ToString());
            var content = doc.RootElement.GetProperty("content").GetString();
            Assert.Contains("safe_name", content);
            Assert.DoesNotContain("Filesystem", content);
            Assert.DoesNotContain("Shell", content);
        }
        finally { Console.SetOut(stdout); MuxConsole.StdioMode = previous; }
    }

    [Fact]
    public void GlobalCatalog_DefaultNativeGroupsAndDisabledScopesAreAccurate()
    {
        var config = App.Config;
        try
        {
            App.Config = new AppConfig();
            var enabled = ToolCatalog.Global();
            Assert.Contains(enabled.Entries, e => e.Kind == "Native" && e.Group == "Shell");
            App.Config.McpServers["Shell"] = new McpServerConfig { Enabled = false };
            var disabled = ToolCatalog.Global();
            Assert.DoesNotContain(disabled.Entries, e => e.Kind == "Native" && e.Group == "Shell");
            Assert.Contains(disabled.Entries, e => e.Kind == "Native" && e.Group == "Filesystem");
        }
        finally { App.Config = config; }
    }

    [Fact]
    public void RemovingExternalTool_RepublishesMenuCatalog()
    {
        var externalField = typeof(ToolCatalog).GetField("_external", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var loadingField = typeof(ToolCatalog).GetField("_loading", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var saved = externalField.GetValue(null);
        var loading = loadingField.GetValue(null);
        try
        {
            var first = Microsoft.Extensions.AI.AIFunctionFactory.Create(() => "unused", "server_first", "First");
            var second = Microsoft.Extensions.AI.AIFunctionFactory.Create(() => "unused", "server_second", "Second");
            ToolCatalog.RegisterExternal(first, "server"); ToolCatalog.RegisterExternal(second, "server");
            var tools = new List<Microsoft.Extensions.AI.AITool> { first, second };
            ToolCatalog.PublishExternal(tools, false);
            Assert.Contains(ToolCatalog.Global().Entries, e => e.Name == "server_first");
            tools.RemoveAt(0);
            ToolCatalog.PublishExternal(tools, false);
            Assert.DoesNotContain(ToolCatalog.Global().Entries, e => e.Name == "server_first");
            Assert.Contains(ToolCatalog.Global().Entries, e => e.Name == "server_second");
            string app = File.ReadAllText(Path.Combine(SourceRoot(), "App.cs"));
            int start = app.IndexOf("private void DisableTools()", StringComparison.Ordinal);
            Assert.Contains("ToolCatalog.PublishExternal(McpTools, loading: false);", app[start..]);
        }
        finally { externalField.SetValue(null, saved); loadingField.SetValue(null, loading); }
    }

    private static string SourceRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));

    [Fact]
    public void PreviewDistinguishesLoadingUninitializedAndEmpty()
    {
        var unknown = new ToolCatalog.Snapshot("session", Array.Empty<ToolCatalog.Entry>(), false);
        Assert.Contains(TuiComponents.ToolsPreview("", unknown, 100), r => r.Contains("not initialized"));
        var empty = new ToolCatalog.Snapshot("session", Array.Empty<ToolCatalog.Entry>());
        Assert.Contains(TuiComponents.ToolsPreview("", empty, 100), r => r.Contains("No tools available in this scope"));
    }

    private sealed class CapturingTerminal : ITuiTerminal
    {
        public int Width => 100;
        public int Height => 24;
        public System.Text.StringBuilder Output { get; } = new();
        public void Write(string text) => Output.Append(text);
        public void Flush() { }
    }

    [Fact]
    public void IdleGlobalPreview_RepaintsWhenCatalogPublicationChanges()
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var external = typeof(ToolCatalog).GetField("_external", flags)!;
        var loading = typeof(ToolCatalog).GetField("_loading", flags)!;
        var savedExternal = external.GetValue(null);
        var savedLoading = loading.GetValue(null);
        var term = new CapturingTerminal();
        var driver = new TuiDriver(term, frameEngine: true);
        var instance = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        try
        {
            ToolCatalog.PublishExternal(Array.Empty<Microsoft.Extensions.AI.AITool>(), true);
            driver.SetToolsCatalogProvider(ToolCatalog.Global);
            typeof(TuiDriver).GetField("_inInput", instance)!.SetValue(driver, true);
            var editor = (LineEditor)typeof(TuiDriver).GetField("_editor", instance)!.GetValue(driver)!;
            editor.SetBuffer("/tools arrived");
            driver.PollResize(); // record initial dimensions
            term.Output.Clear();
            var tool = Microsoft.Extensions.AI.AIFunctionFactory.Create(() => "unused", "arrived_tool", "Connected now");
            ToolCatalog.RegisterExternal(tool, "Test_Server");
            ToolCatalog.PublishExternal(new[] { tool }, false);
            driver.PollResize();
            Assert.Contains("arrived_tool", term.Output.ToString());
            term.Output.Clear();
            driver.PollResize();
            Assert.Equal("", term.Output.ToString());
        }
        finally { external.SetValue(null, savedExternal); loading.SetValue(null, savedLoading); }
    }
}
