using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>Composer Actions menu: user-chosen commands from the engine catalog (Settings > Composer actions).</summary>
public class WebUiComposerActionsTests
{
    private static readonly string Html = File.ReadAllText(Path.Combine(RepoRoot(), "Runtime", "mux-web-app", "index.html"));

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "MuxSwarm.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException("MuxSwarm.csproj not found above the test output");
    }

    [Fact]
    public void Defaults_AddPrune_AndDropWipe()
    {
        Assert.Contains("composerActions: ['/compact', '/prune', '/undo', '/retry', '/copy'],", Html);
        Assert.DoesNotContain("['Wipe context\\u2026', '/wipe', true]", Html);
    }

    [Fact]
    public void Menu_ReadsTheSettingsList_AndTheEngineCatalog()
    {
        Assert.Contains("window.MuxCommands = { all: () => SLASH_COMMANDS.map(c => Object.assign({}, c)) };", Html);
        Assert.Contains(".map(c => ({ cmd: c.cmd, desc: c.desc || '', scope: c.scope || '' }));", Html);
        Assert.Contains("o.disabled = c.scope === 'session' && !inAgent;", Html);
        Assert.Contains("section('Composer actions', [actionsPicker()]);", Html);
    }
}
