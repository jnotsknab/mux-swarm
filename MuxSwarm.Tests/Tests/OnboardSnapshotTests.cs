using MuxSwarm.Engine;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// /onboard may now edit Config.json / Swarm.json / BRAIN.md / MEMORY.md, so it backs them up first and
// /onboard undo restores the newest backup.
public sealed class OnboardSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mux-onboard-snap-" + Guid.NewGuid().ToString("N")[..8]);
    private string Cfg => Path.Combine(_root, "Configs", "Config.json");
    private string Swarm => Path.Combine(_root, "Configs", "Swarm.json");
    private string Ctx => Path.Combine(_root, "Context");
    private string BackupRoot => Path.Combine(_root, "Configs");

    public OnboardSnapshotTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Configs"));
        Directory.CreateDirectory(Ctx);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void TakeThenRestore_PutsEditedFilesBack()
    {
        File.WriteAllText(Cfg, "{\"a\":1}");
        File.WriteAllText(Swarm, "{\"s\":1}");
        File.WriteAllText(Path.Combine(Ctx, "BRAIN.md"), "brain v1");

        var dir = OnboardSnapshot.Take(OnboardSnapshot.Files(Cfg, Swarm, Ctx), BackupRoot, new DateTime(2026, 10, 28, 1, 2, 3, DateTimeKind.Utc));
        Assert.NotNull(dir);
        Assert.StartsWith(OnboardSnapshot.Prefix, Path.GetFileName(dir));

        // Onboarding edits everything, and creates MEMORY.md, which did not exist at backup time.
        File.WriteAllText(Cfg, "{\"a\":2}");
        File.WriteAllText(Swarm, "{\"s\":2}");
        File.WriteAllText(Path.Combine(Ctx, "BRAIN.md"), "brain v2");
        File.WriteAllText(Path.Combine(Ctx, "MEMORY.md"), "new");

        var restored = OnboardSnapshot.Restore(OnboardSnapshot.Latest(BackupRoot)!);
        Assert.Equal(3, restored.Count);
        Assert.Equal("{\"a\":1}", File.ReadAllText(Cfg));
        Assert.Equal("{\"s\":1}", File.ReadAllText(Swarm));
        Assert.Equal("brain v1", File.ReadAllText(Path.Combine(Ctx, "BRAIN.md")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(Ctx, "MEMORY.md")));   // not in the backup: left alone
    }

    [Fact]
    public void Latest_PicksTheNewestBackup()
    {
        File.WriteAllText(Cfg, "old");
        OnboardSnapshot.Take(OnboardSnapshot.Files(Cfg, Swarm, Ctx), BackupRoot, new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc));
        File.WriteAllText(Cfg, "newer");
        var second = OnboardSnapshot.Take(OnboardSnapshot.Files(Cfg, Swarm, Ctx), BackupRoot, new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(second, OnboardSnapshot.Latest(BackupRoot));
        File.WriteAllText(Cfg, "edited");
        OnboardSnapshot.Restore(second!);
        Assert.Equal("newer", File.ReadAllText(Cfg));
    }

    [Fact]
    public void NothingToBackUp_ReturnsNull_AndNoLatest()
    {
        Assert.Null(OnboardSnapshot.Take(OnboardSnapshot.Files(Cfg, Swarm, Ctx), BackupRoot, DateTime.UtcNow));
        Assert.Null(OnboardSnapshot.Latest(BackupRoot));
        Assert.Null(OnboardSnapshot.Latest(Path.Combine(_root, "missing")));
    }

    [Theory]
    [InlineData(null, 200_000)]
    [InlineData(0, 200_000)]
    [InlineData(1_000_000, 1_000_000)]
    [InlineData(64_000, 64_000)]
    public void OnboardSession_CompactsAt200k_UnlessTheUserSetAThreshold(int? configured, int expected) =>
        Assert.Equal(expected, CliCmdUtils.OnboardCompactThreshold(configured));   // the general fallback is 80k

    [Fact]
    public void OnboardSession_HasTheNormalTurnBudget() =>
        Assert.True(CliCmdUtils.OnboardMaxIterations >= 15);   // 3 ended onboarding mid-setup

    [Fact]
    public void Playbook_PutsTheUsersQuestionsBeforeTheFlow()
    {
        // A question typed as a custom ask_user answer was treated as a side note and the flow moved on.
        string root = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(root); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "MuxSwarm.csproj"))) { root = d.FullName; break; }
        var playbook = File.ReadAllText(Path.Combine(root, "Context", "ONBOARD.md"));
        Assert.Contains("The user's own words come first, always.", playbook);
        Assert.Contains("park the flow and handle that first", playbook);
        Assert.Contains("is never a\n  choice", playbook.Replace("\r\n", "\n"));
    }

    [Fact]
    public void DocsToken_PointsAtLocalDocs()
    {
        var resolved = TokenInjector.InjectTokens("read {{paths.docs}}");
        Assert.Equal("read " + Path.Combine(PlatformContext.ContextDirectory, "DOCS.md"), resolved);
    }
}
