using System;
using System.Collections.Generic;
using System.IO;
using MuxSwarm.Engine;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// v0.15.2 CLI fixes. #4: --version/-V and --help/-h exit from Program.Main before App() exists (so no
/// Configs/Config.json write and no interactive setup). #7: relative --cfg/--swarmcfg resolve against the
/// directory the user launched from, not the install dir the shim cd's into.
/// </summary>
public class CliEarlyFlagsAndCfgPathTests
{
    [Theory]
    [InlineData("--version")]
    [InlineData("-V")]
    [InlineData("--Version")]
    [InlineData("--VERSION")]
    public void Version_PrintsVersionAndExitsZero(string flag)
    {
        var sw = new StringWriter();
        Assert.Equal(0, Program.TryHandleEarlyFlag(new[] { flag }, sw));
        Assert.Equal($"mux-swarm {App.Version}", sw.ToString().Trim());
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("--HELP")]
    [InlineData("--Help")]
    public void Help_PrintsHelpTextAndExitsZero(string flag)
    {
        var sw = new StringWriter { NewLine = Environment.NewLine };
        Assert.Equal(0, Program.TryHandleEarlyFlag(new[] { "--serve", flag }, sw));
        var expected = Help.HelpText.Replace("\r\n", "\n").Replace("\n", Environment.NewLine) + Environment.NewLine;
        Assert.Equal(expected, sw.ToString());
        if (Environment.NewLine == "\n")
            Assert.DoesNotContain("\r", sw.ToString());
    }

    [Theory]
    [InlineData]
    [InlineData("--serve", "6723")]
    [InlineData("--bogus-flag")]          // unknown flags stay a warning in ParseArgs, not an early exit
    [InlineData("-v")]                    // case-sensitive: -V only
    [InlineData("-H")]                    // short flags stay case-sensitive
    public void OtherArgs_ContinueNormalStartup(params string[] args)
    {
        var sw = new StringWriter();
        Assert.Null(Program.TryHandleEarlyFlag(args, sw));
        Assert.Equal("", sw.ToString());
    }

    [Fact]
    public void Help_ListsTheVersionFlag() => Assert.Contains("--version, -V", Help.HelpText);

    private static readonly string Install = OperatingSystem.IsWindows()
        ? @"C:\Users\x\AppData\Local\Mux-Swarm" : "/home/x/.local/share/mux-swarm";
    private static readonly string Project = OperatingSystem.IsWindows()
        ? @"C:\Users\x\code\myproject" : "/home/x/code/myproject";

    [Fact]
    public void RelativeCfg_ResolvesAgainstMuxLaunchCwd_WhenShimCdsIntoInstallDir()
    {
        // The shim cd's into the install dir and exports MUX_LAUNCH_CWD; the user typed `--cfg Config.x.json`
        // in their project. Old code resolved it against the process CWD (the install dir) and failed.
        var env = new Dictionary<string, string?> { ["MUX_LAUNCH_CWD"] = Project };
        string launch = PlatformContext.ResolveLaunchCwd(Install, Install, n => env.TryGetValue(n, out var v) ? v : null, _ => true);
        string expected = Path.Combine(Project, "Config.x.json");
        string got = PlatformContext.ResolveUserPath("Config.x.json", launch, Install, p => p == expected);
        Assert.Equal(expected, got);
    }

    [Fact]
    public void RelativeCfg_FallsBackToCwd_WhenOnlyThereExists()
    {
        string inCwd = Path.Combine(Install, "Config.x.json");
        Assert.Equal(inCwd, PlatformContext.ResolveUserPath("Config.x.json", Project, Install, p => p == inCwd));
    }

    [Fact]
    public void RelativeCfg_Missing_ReportsTheLaunchDirPath()
    {
        Assert.Equal(Path.Combine(Project, "nope.json"),
            PlatformContext.ResolveUserPath("nope.json", Project, Install, _ => false));
    }

    [Fact]
    public void AbsoluteCfg_IsUnchanged()
    {
        string abs = Path.Combine(Project, "sub", "Config.json");
        Assert.Equal(abs, PlatformContext.ResolveUserPath(abs, Install, Install, _ => false));
    }

    [Fact]
    public void ApplyOverrides_MissingRelativeCfg_ErrorCarriesTheFullResolvedPath()
    {
        // Throws before any static override is assigned, so no shared state is touched.
        string rel = "mux-missing-" + Guid.NewGuid().ToString("N") + ".json";
        var ex = Assert.Throws<FileNotFoundException>(() => PlatformContext.ApplyOverrides(rel));
        Assert.True(Path.IsPathRooted(ex.FileName));
        Assert.EndsWith(rel, ex.FileName);
        Assert.Contains(ex.FileName!, ex.Message);
    }
}
