using System.Text.Json;
using MuxSwarm.Engine.Lsp;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// The LSP client layer against a scripted server: config matching, root detection, URI round-trips,
// full-text sync with increasing versions, diagnostics that are pending (never clean) on a timeout,
// failed-start memory, and Reset stopping the servers Mux started. LspManager is process-global.
[Collection("ConsoleState")]
public sealed class LspClientTests : IDisposable
{
    private readonly LspTestRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private async Task<(LspClient Client, string Uri, int? Version)> Open(string path)
    {
        var (client, languageId, why) = await LspManager.ClientForAsync(path, CancellationToken.None);
        Assert.True(client is not null, why);
        return (client!, LspUri.FromPath(path), await client!.SyncAsync(path, languageId));
    }

    [Fact]
    public async Task Off_NothingStarts()
    {
        _rig.Use(enabled: false);
        Assert.False(LspManager.Active);
        var (client, _, why) = await LspManager.ClientForAsync(_rig.Src("a.fake", "x"), CancellationToken.None);
        Assert.Null(client);
        Assert.Contains("LSP is off", why);
    }

    [Fact]
    public void Matching_ExtensionsWithOrWithoutDot_LanguageIds_RootMarkerGlob()
    {
        _rig.Use();
        var a = _rig.Src("a.fake", "x");
        Assert.Equal("fake", LspManager.ServerFor(a)?.Name);
        Assert.Equal("fake", LspManager.ServerFor(_rig.Src("b.FK", "x"))?.Name);
        Assert.Null(LspManager.ServerFor(_rig.Src("c.txt", "x")));
        var server = LspManager.ServerFor(a)!.Value.Server;
        Assert.Equal("fakelang", LspManager.LanguageId(server, "z.fk"));
        Assert.Equal("fake", LspManager.LanguageId(server, a));
        Assert.Equal(Path.Combine(_rig.Dir, "proj"), LspManager.RootFor(server, a));
        Assert.Equal(Path.GetFullPath(_rig.Dir), LspManager.RootFor(new LspServerConfig { RootMarkers = ["nope.cfg"] }, a));
    }

    [Theory]
    [InlineData("dir with space")]
    [InlineData("caf\u00e9")]
    public void Uri_RoundTripsLocalPaths(string folder)
    {
        var p = Path.Combine(_rig.Dir, folder, "x.fake");
        var uri = LspUri.FromPath(p);
        Assert.StartsWith("file:///", uri);
        Assert.DoesNotContain(" ", uri);
        Assert.Equal(Path.GetFullPath(p), LspUri.ToPath(uri));
        if (OperatingSystem.IsWindows())
            Assert.True(LspUri.Same(uri, uri.Replace("file:///" + uri[8], "file:///" + char.ToLowerInvariant(uri[8]))));
    }

    [Fact]
    public async Task Sync_FullTextWithIncreasingVersion_AndFreshDiagnosticsAfterAnEdit()
    {
        _rig.Use();
        var f = _rig.Src("a.fake", "line one\nhas BUG here\nfine");
        var (client, uri, v1) = await Open(f);
        Assert.Equal(1, v1);
        var first = await LspDiagnostics.WaitAsync(client, uri, v1, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("a.fake", Path.GetFileName(LspUri.ToPath(uri)));
        Assert.Equal(1, LspDiagnostics.Errors(first!.Value));

        Assert.Equal(1, await client.SyncAsync(f, "fake"));   // unchanged text: no new version
        File.WriteAllText(f, "line one\nfixed\nfine\nnew BUG\nand BUG\n");
        var v2 = await client.SyncAsync(f, "fake");
        Assert.Equal(2, v2);
        var second = await LspDiagnostics.WaitAsync(client, uri, v2, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(2, LspDiagnostics.Errors(second!.Value));
    }

    [Fact]
    public async Task Diagnostics_Timeout_IsNull_NeverAnEmptyList()
    {
        _rig.SetEnv("FAKE_LSP_NO_DIAG", "1");
        _rig.Use();
        var (client, uri, v) = await Open(_rig.Src("a.fake", "has BUG"));
        Assert.Null(await LspDiagnostics.WaitAsync(client, uri, v, TimeSpan.FromMilliseconds(300), CancellationToken.None));
    }

    [Fact]
    public async Task Initialize_SendsRootUriOfTheMarkedProject()
    {
        _rig.Use();
        await Open(_rig.Src("a.fake", "x"));
        var init = JsonDocument.Parse(File.ReadAllText(Path.Combine(_rig.Dir, "init.json"))).RootElement;
        Assert.Equal(LspUri.FromPath(Path.Combine(_rig.Dir, "proj")), init.GetProperty("rootUri").GetString());
    }

    [Fact]
    public async Task MissingCommand_FailsOnce_IsRemembered_UntilReset()
    {
        _rig.Use(command: "definitely-not-a-language-server-" + Guid.NewGuid().ToString("N")[..6]);
        var f = _rig.Src("a.fake", "x");
        Assert.Contains("not found on PATH", (await LspManager.ClientForAsync(f, CancellationToken.None)).Why);
        Assert.Contains("failed to start", (await LspManager.ClientForAsync(f, CancellationToken.None)).Why);
        Assert.Contains(LspManager.Status(), l => l.Contains("failed in"));
        LspManager.Reset();
        Assert.DoesNotContain(LspManager.Status(), l => l.Contains("failed in"));
    }

    [Fact]
    public async Task Reset_StopsTheServersItStarted()
    {
        _rig.Use();
        var (client, _, _) = await Open(_rig.Src("a.fake", "x"));
        Assert.Single(LspManager.Running);
        LspManager.Reset();
        Assert.False(client.IsRunning);
        Assert.Empty(LspManager.Running);
    }
}
