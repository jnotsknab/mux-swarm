using MuxSwarm.Engine.Extensions;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// Extension command metadata: usage hints and subcommands are validated from the manifest and
// become palette rows (the TUI palette, /api/commands and suggestions all read Entries).
[Collection("ConsoleState")]
public sealed class ExtensionCommandMetaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mux-cmdmeta-" + Guid.NewGuid().ToString("N"));

    public ExtensionCommandMetaTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        ExtensionCommands.Set([]);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private ExtensionRegistry.Result Load(string manifest)
    {
        var dir = Path.Combine(_root, "crm");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "extension.json"), manifest);
        return ExtensionRegistry.Scan([_root]);
    }

    [Fact]
    public void UsageAndSubcommands_BecomePaletteRows()
    {
        var result = Load("""
            { "process": { "command": "x" },
              "commands": [ { "name": "contacts", "description": "Manage contacts", "usage": "<add|list> [name]",
                              "subcommands": [ { "name": "Add", "description": "Add a contact" }, { "name": "list" } ] },
                            { "name": "plain", "description": "No extras" } ] }
            """);
        Assert.Empty(result.Problems);
        ExtensionCommands.Set(result.Extensions.SelectMany(e => e.Commands));

        Assert.Equal(
        [
            ("/contacts", "Manage contacts  <add|list> [name] [crm]"),
            ("/contacts add", "Add a contact"),
            ("/contacts list", "[crm]"),
            ("/plain", "No extras [crm]"),
        ], ExtensionCommands.Entries);
    }

    [Fact]
    public void SubcommandRow_RunsTheParentCommand()
    {
        var result = Load("""{ "process": { "command": "x" }, "commands": [ { "name": "contacts", "subcommands": [ { "name": "add" } ] } ] }""");
        ExtensionCommands.Set(result.Extensions.SelectMany(e => e.Commands));
        Assert.Equal("contacts", ExtensionCommands.Match("/contacts add Ann")?.Name);
    }

    [Fact]
    public void BadSubcommands_AreReportedAndDropped()
    {
        var subs = string.Join(",", Enumerable.Range(0, 26).Select(i => $$"""{ "name": "s{{i}}" }"""));
        var result = Load($$"""
            { "process": { "command": "x" },
              "commands": [ { "name": "a", "usage": "{{new string('u', 80)}}",
                              "subcommands": [ { "name": "Bad Name" }, { "name": "dup" }, { "name": "dup" }, {{subs}} ] } ] }
            """);
        var cmd = Assert.Single(Assert.Single(result.Extensions).Commands);
        Assert.Equal(ExtensionRegistry.MaxSubcommands, cmd.Subcommands.Count);
        Assert.Equal(60, cmd.Usage.Length);
        Assert.Contains(result.Problems, p => p.Contains("invalid subcommand 'Bad Name'"));
        Assert.Contains(result.Problems, p => p.Contains("duplicate subcommand 'dup'"));
        Assert.Contains(result.Problems, p => p.Contains($"more than {ExtensionRegistry.MaxSubcommands} subcommands"));
    }
}
