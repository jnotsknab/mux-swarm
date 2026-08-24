using System;
using System.Collections.Generic;
using System.IO;
using MuxSwarm.Utils;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Covers PlatformContext.ResolveLaunchCwd - recovering the directory the user launched Mux FROM when the
/// install shim/alias cd's into the install dir before invoking the exe (so the @ file picker indexes the
/// real project, not mux's own install files). Pure-function tests via the injected-seam overload.
/// </summary>
public class WorkspaceLaunchCwdResolutionTests
{
    // Platform-native paths: Path.GetFullPath and separator handling treat Windows drive
    // literals as plain name chars on Linux/macOS CI runners, so Windows-only literals
    // silently stop exercising the logic under test off-Windows.
    private static readonly string Install = OperatingSystem.IsWindows()
        ? @"C:\Users\x\AppData\Local\Mux-Swarm" : "/home/x/.local/share/mux-swarm";
    private static readonly string Project = OperatingSystem.IsWindows()
        ? @"C:\Users\x\code\myproject" : "/home/x/code/myproject";

    private static Func<string, string?> Env(Dictionary<string, string?> map) =>
        name => map.TryGetValue(name, out var v) ? v : null;

    [Fact]
    public void DirectLaunch_CwdNotInstall_ReturnsCwd_IgnoringEnv()
    {
        // cwd != install => a direct binary launch; the real cwd is correct and a stale OLDPWD must NOT win.
        var env = Env(new()
        {
            ["OLDPWD"] = OperatingSystem.IsWindows() ? @"C:\somewhere\else" : "/somewhere/else",
            ["MUX_LAUNCH_CWD"] = OperatingSystem.IsWindows() ? @"C:\other" : "/other",
        });
        var got = PlatformContext.ResolveLaunchCwd(Project, Install, env, _ => true);
        Assert.Equal(Project, got);
    }

    [Fact]
    public void ShimLaunch_RecoversFrom_MuxLaunchCwd_First()
    {
        // cwd == install (shim cd'd us here). MUX_LAUNCH_CWD takes precedence over OLDPWD.
        var env = Env(new()
        {
            ["MUX_LAUNCH_CWD"] = Project,
            ["OLDPWD"] = OperatingSystem.IsWindows() ? @"C:\Users\x\other" : "/home/x/other",
        });
        var got = PlatformContext.ResolveLaunchCwd(Install, Install, env, d => true);
        Assert.Equal(Path.GetFullPath(Project), got);
    }

    [Fact]
    public void ShimLaunch_RecoversFrom_Oldpwd_WhenNoLaunchCwd()
    {
        // Linux/macOS: bash's cd exports OLDPWD; no shim change needed.
        var env = Env(new() { ["OLDPWD"] = Project });
        var got = PlatformContext.ResolveLaunchCwd(Install, Install, env, d => true);
        Assert.Equal(Path.GetFullPath(Project), got);
    }

    [Fact]
    public void ShimLaunch_NoSignal_FallsBackToCwd()
    {
        // cwd == install but nothing to recover (e.g. Windows with the unmodified shim): return cwd.
        var env = Env(new());
        var got = PlatformContext.ResolveLaunchCwd(Install, Install, env, d => true);
        Assert.Equal(Install, got);
    }

    [Fact]
    public void ShimLaunch_CandidateEqualsInstall_IsIgnored()
    {
        // Launched from the install dir itself: OLDPWD == install => nothing to recover, keep cwd.
        var env = Env(new() { ["OLDPWD"] = Install });
        var got = PlatformContext.ResolveLaunchCwd(Install, Install, env, d => true);
        Assert.Equal(Install, got);
    }

    [Fact]
    public void ShimLaunch_NonexistentCandidate_IsSkipped()
    {
        // A candidate dir that no longer exists is skipped; falls through to the next / cwd.
        string real = OperatingSystem.IsWindows() ? @"C:\real" : "/real";
        var env = Env(new() { ["MUX_LAUNCH_CWD"] = Project, ["OLDPWD"] = real });
        var got = PlatformContext.ResolveLaunchCwd(
            Install, Install, env, d => d.Equals(Path.GetFullPath(real), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Path.GetFullPath(real), got);
    }

    [Fact]
    public void ShimLaunch_TrailingSlashInstall_StillDetectsInstallCwd()
    {
        // BaseDirectory often ends with a separator; the cwd==install comparison must be slash-insensitive.
        var env = Env(new() { ["OLDPWD"] = Project });
        var got = PlatformContext.ResolveLaunchCwd(Install, Install + Path.DirectorySeparatorChar, env, d => true);
        Assert.Equal(Path.GetFullPath(Project), got);
    }
}
