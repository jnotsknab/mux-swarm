using System;
using System.Collections.Generic;
using System.IO;
using MuxSwarm;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Covers App.ApplyAutoAllowWorkspace - auto-injecting the resolved workspace root into the native
/// Filesystem AllowedPaths at startup (executionLimits.autoAllowWorkspace, default true). Pure logic via
/// the injected-seam overload; no static state or real filesystem touched.
/// </summary>
public class AutoAllowWorkspaceTests
{
    private const string Ws = @"C:\Users\x\code\proj";

    [Fact]
    public void Enabled_AddsWorkspace_WhenMissing()
    {
        var allowed = new List<string> { @"C:\some\other" };
        bool added = App.ApplyAutoAllowWorkspace(allowed, enabled: true, workspaceIsInstallDir: false, Ws, _ => true);
        Assert.True(added);
        Assert.Contains(Path.GetFullPath(Ws), allowed);
    }

    [Fact]
    public void Disabled_DoesNothing()
    {
        var allowed = new List<string>();
        bool added = App.ApplyAutoAllowWorkspace(allowed, enabled: false, workspaceIsInstallDir: false, Ws, _ => true);
        Assert.False(added);
        Assert.Empty(allowed);
    }

    [Fact]
    public void WorkspaceIsInstallDir_DoesNothing()
    {
        // Launched via the shim with nothing to recover -> workspace == install dir -> skip.
        var allowed = new List<string>();
        bool added = App.ApplyAutoAllowWorkspace(allowed, enabled: true, workspaceIsInstallDir: true, Ws, _ => true);
        Assert.False(added);
        Assert.Empty(allowed);
    }

    [Fact]
    public void NonexistentWorkspace_DoesNothing()
    {
        var allowed = new List<string>();
        bool added = App.ApplyAutoAllowWorkspace(allowed, enabled: true, workspaceIsInstallDir: false, Ws, _ => false);
        Assert.False(added);
        Assert.Empty(allowed);
    }

    [Fact]
    public void AlreadyPresent_IsIdempotent()
    {
        var allowed = new List<string> { Ws };
        bool added = App.ApplyAutoAllowWorkspace(allowed, enabled: true, workspaceIsInstallDir: false, Ws, _ => true);
        Assert.False(added);
        Assert.Single(allowed);
    }

    [Fact]
    public void AlreadyPresent_TrailingSlash_StillIdempotent()
    {
        var allowed = new List<string> { Ws + @"\" };
        bool added = App.ApplyAutoAllowWorkspace(allowed, enabled: true, workspaceIsInstallDir: false, Ws, _ => true);
        Assert.False(added);
        Assert.Single(allowed);
    }
}
