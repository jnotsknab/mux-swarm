using System;
using System.Collections.Generic;
using System.Linq;
using MuxSwarm.Engine;
using MuxSwarm.Engine.NativeTools;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Unit coverage for the pluggable sandbox backend resolver + validator. Pure config -> spec logic;
/// does NOT spawn containers (those need a live docker daemon and are validated interactively).
/// Backends whose binary is absent on the test host resolve to a validation ERROR, which is itself the
/// contract under test for the "missing binary => hard error, never silent host fallback" rule.
/// </summary>
[Collection("ConsoleState")]
public class SandboxBackendTests
{
    private static SandboxConfig Cfg(string backend, string image = "python:3.12-slim",
        bool network = false, List<string>? allow = null, string command = "") =>
        new() { Backend = backend, Image = image, Network = network, AllowedDomains = allow ?? new(), Command = command };

    [Fact]
    public void Host_ResolvesToNull_NoSandbox()
    {
        Assert.Null(SandboxBackend.Resolve(Cfg("host")));
        Assert.Null(SandboxBackend.Resolve(Cfg("")));
        Assert.Null(SandboxBackend.Resolve(Cfg("none")));
        Assert.Null(SandboxBackend.Validate(Cfg("host")));   // host is always valid
    }

    [Fact]
    public void AllowlistProxy_RunsFromFixedImage_NotTheSandboxImage()
    {
        // The proxy sidecar used to run `python` from the SANDBOX image, so a python-less image (dotnet/sdk)
        // made it exit 127 and left the sandbox with no network at all.
        string args = OciSandbox.ProxyRunArgs("mux_sbxproxy_x", "mux_sbxnet_x", "QUJD");
        Assert.Contains($"--entrypoint sh {OciSandbox.ProxyImage} -c", args);
        Assert.Contains("--network mux_sbxnet_x", args);
        Assert.StartsWith("docker.io/library/python:", OciSandbox.ProxyImage); // fully qualified for podman
    }

    [Fact]
    public void AllowlistProxyScript_EmbedsAValidListLiteral()
    {
        // Regression: domains were wrapped in \" (left over from shell quoting), which made the generated
        // python `ALLOW = [\"api.nuget.org\"]` a syntax error, so the proxy never started.
        string script = OciSandbox.BuildProxyScript(new[] { "api.nuget.org", "www.nuget.org" });
        Assert.Contains("ALLOW = [\"api.nuget.org\",\"www.nuget.org\"]\n", script);
        Assert.DoesNotContain("\\\"", script.Split('\n').First(l => l.StartsWith("ALLOW = ")));
        Assert.DoesNotContain("\r", script);
    }

    [Fact]
    public void AllowlistProxyScript_EscapesQuotesAndBackslashes()
    {
        // The ALLOW literal must stay valid (JSON == python string list) for hostile domain text.
        var domains = new[] { "a\"b.example", "c\\d.example" };
        string script = OciSandbox.BuildProxyScript(domains);
        string line = script.Split('\n').First(l => l.StartsWith("ALLOW = "));
        var parsed = System.Text.Json.JsonSerializer.Deserialize<string[]>(line["ALLOW = ".Length..]);
        Assert.Equal(domains, parsed);
    }

    [Theory]
    [InlineData(true, false, false, true)]   // no allowlist: the container alone decides
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]   // proxy died: rebuild
    [InlineData(false, false, false, false)]
    [InlineData(false, true, true, false)]
    public void HealthyFastPath_RequiresProxyWhenAllowlisted(bool containerUp, bool allowlist, bool proxyUp, bool expected)
        => Assert.Equal(expected, OciSandbox.IsHealthy(containerUp, allowlist, proxyUp));

    [Fact]
    public void Run_EnforcesTimeout_OnAHangingCommand()
    {
        // Regression: Run read stdout/stderr to the end BEFORE WaitForExit, so the timeout never fired.
        var (file, args) = OperatingSystem.IsWindows()
            ? ("powershell", "-NoProfile -Command Start-Sleep 10")
            : ("sleep", "10");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (ok, _, err) = OciSandbox.Run(file, args, allowFail: true, timeoutMs: 500);
        sw.Stop();
        Assert.False(ok);
        Assert.Equal("timed out", err);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"took {sw.Elapsed}");
    }

    [Fact]
    public void UnknownBackend_IsRejected()
    {
        var err = SandboxBackend.Validate(Cfg("garbage-backend"));
        Assert.NotNull(err);
        Assert.Contains("Unknown sandbox.backend", err);
    }

    [Fact]
    public void Custom_RequiresTemplate()
    {
        var err = SandboxBackend.Validate(Cfg("custom", command: ""));
        Assert.NotNull(err);
        Assert.Contains("template", err);
        // With a template it resolves (custom never probes a binary).
        Assert.Null(SandboxBackend.Validate(Cfg("custom", command: "docker run {image} sh -c {cmd}")));
    }

    [Fact]
    public void Custom_RejectsAllowlist()
    {
        var err = SandboxBackend.Validate(Cfg("custom", command: "x {cmd}", allow: new() { "pypi.org" }));
        Assert.NotNull(err);
        Assert.Contains("container backends", err);
    }

    [Fact]
    public void Allowlist_OnWrapperBackend_IsRejected()
    {
        // bwrap/firejail are Linux-only; on a non-Linux host the OS check may fire first, but EITHER way
        // an allowlist on a wrapper backend must NOT validate.
        Assert.NotNull(SandboxBackend.Validate(Cfg("bwrap", allow: new() { "pypi.org" })));
        Assert.NotNull(SandboxBackend.Validate(Cfg("firejail", allow: new() { "pypi.org" })));
    }

    [Fact]
    public void WrapperBackends_AreOsGated()
    {
        if (OperatingSystem.IsWindows())
        {
            // None of the wrapper backends are valid on Windows.
            Assert.NotNull(SandboxBackend.Validate(Cfg("bwrap")));
            Assert.NotNull(SandboxBackend.Validate(Cfg("firejail")));
            Assert.NotNull(SandboxBackend.Validate(Cfg("sandbox-exec")));
        }
    }

    [Fact]
    public void CustomSpec_RendersTemplate()
    {
        var spec = SandboxBackend.Resolve(Cfg("custom", image: "myimg", command: "run {image} :: {workdir} :: {cmd}"));
        Assert.NotNull(spec);
        Assert.Equal(SandboxKind.Custom, spec!.Kind);
        var (file, args) = SandboxBackend.WrapShellCommand(spec, "echo hi", "/work/dir");
        // The rendered template (after placeholder substitution) appears in the shell-wrapped args.
        Assert.Contains("myimg", args);
        Assert.Contains("/work/dir", args);
        Assert.Contains("echo hi", args);
    }

    [Theory]
    [InlineData("docker-container")]
    [InlineData("docker-legacy")]
    public void OciBackend_MissingBinary_IsHardError_NeverSilentHost(string backend)
    {
        // If docker is not installed/ready on the test host, validation MUST return an error (the
        // anti-silent-fallback contract). If docker IS present+ready, it validates - both are correct;
        // the invariant is "never null-with-an-unusable-backend".
        var err = SandboxBackend.Validate(Cfg(backend));
        if (err is not null)
            Assert.True(err.Contains("docker", StringComparison.OrdinalIgnoreCase));
        else
        {
            var spec = SandboxBackend.Resolve(Cfg(backend))!;
            Assert.Equal(SandboxKind.Oci, spec.Kind);
            Assert.Equal("docker-container", spec.Backend);   // docker-legacy is an alias
            Assert.Equal("docker", spec.Binary);
        }
    }

    [Fact]
    public void Docker_IsTheSbxMicroVm_OrAHardError()
    {
        // `docker` is the Docker Sandboxes microVM. Without a ready sbx it is an error naming sbx and the
        // container escape hatch - never a silent fall back to a plain container or the host.
        var err = SandboxBackend.Validate(Cfg("docker"));
        if (err is not null)
        {
            Assert.Contains("sbx", err);
            Assert.Contains("docker-container", err);
        }
        else
            Assert.Equal(SandboxKind.Sbx, SandboxBackend.Resolve(Cfg("docker"))!.Kind);
    }

    [Fact]
    public void Docker_RejectsAllowlist_BeforeProbingSbx()
    {
        // sbx deny rules outrank allow rules, so a strict allowlist is not expressible: fail loud.
        var err = SandboxBackend.Validate(Cfg("docker", allow: new() { "pypi.org" }));
        Assert.NotNull(err);
        Assert.Contains("allowedDomains", err);
        Assert.Contains("docker-container", err);
    }

    [Theory]
    [InlineData("docker-legacy", "docker-container")]
    [InlineData(" Docker-Legacy ", "docker-container")]
    [InlineData("docker", "docker")]
    [InlineData(null, "host")]
    public void Canonical_MapsLegacyAlias(string? input, string expected) =>
        Assert.Equal(expected, SandboxBackend.Canonical(input));

    [Theory]
    [InlineData("gvisor")]
    [InlineData("kata")]
    public void HardenedRuntimes_StayOnTheContainerEngine(string backend)
    {
        // gvisor/kata layer a runtime onto docker containers; they must not route to sbx.
        var err = SandboxBackend.Validate(Cfg(backend));
        if (err is null) Assert.Equal(SandboxKind.Oci, SandboxBackend.Resolve(Cfg(backend))!.Kind);
        else Assert.DoesNotContain("sbx", err);
    }

    [Theory]
    [InlineData("repl_abc", "mux-repl-abc-1a2b3c")]
    [InlineData("__primary__", "mux-primary-1a2b3c")]
    [InlineData("", "mux-s-1a2b3c")]
    [InlineData("Web Agent/#2", "mux-web-agent--2-1a2b3c")]
    public void SbxName_IsLowercaseHyphenated_NoUnderscores(string key, string expected)
    {
        string name = SbxSandbox.SandboxName(key, "1a2b3c");
        Assert.Equal(expected, name);
        Assert.DoesNotContain('_', name);
    }

    [Fact]
    public void SbxMounts_DropNetworkShares_KeepTheRest()
    {
        var m = SandboxBackend.SbxMounts(Fs("standard", @"C:\proj", @"\\nas\share\docs", "//nas/share/x", @"C:\refs"));
        Assert.Equal(new[] { @"C:\proj", @"C:\refs" }, m.Select(x => x.HostPath));
        Assert.False(m[0].ReadOnly);   // workspace posture is unchanged by the filter
    }

    [Theory]
    [InlineData(true, false)]    // still listed: keep it
    [InlineData(null, false)]    // `sbx ls` failed: unknown, keep it (never leak or split the session)
    [InlineData(false, true)]    // confirmed gone: rebuild
    public void SbxRebuild_OnlyWhenConfirmedGone(bool? exists, bool rebuild) =>
        Assert.Equal(rebuild, SbxSandbox.ShouldRebuild(exists));

    [Fact]
    public void SbxName_IsLengthBounded()
    {
        string name = SbxSandbox.SandboxName(new string('a', 200), "1a2b3c");
        Assert.True(name.Length <= "mux-".Length + 24 + "-1a2b3c".Length);
    }

    [Theory]
    [InlineData(@"C:\Users\me\work", true, "/c/Users/me/work")]
    [InlineData(@"D:\proj\", true, "/d/proj")]
    [InlineData(@"E:\", true, "/e")]
    [InlineData("/home/me/work", false, "/home/me/work")]
    public void SbxGuestPath_MirrorsHostPath(string host, bool windows, string expected) =>
        Assert.Equal(expected, SbxSandbox.GuestPath(host, windows));

    [Fact]
    public void SbxCreateArgs_DeniesEgress_UnlessNetworkOpen_AndMarksReadOnlyMounts()
    {
        var mounts = new[] { new SandboxMount(@"C:\proj", "/host/proj", false), new SandboxMount(@"C:\refs", "/host/refs", true) };
        string closed = SbxSandbox.CreateArgs("mux-x-1", "python:3.12-slim", false, @"C:\w", mounts);
        Assert.Equal("create --name mux-x-1 -q --pull missing -t python:3.12-slim --deny-network \"**\" shell \"C:\\w\" \"C:\\proj\" \"C:\\refs:ro\"", closed);
        string open = SbxSandbox.CreateArgs("mux-x-1", "python:3.12-slim", true, @"C:\w", mounts);
        Assert.DoesNotContain("--deny-network", open);
    }

    [Fact]
    public void SbxLayoutScript_LinksWorkAndHostMounts_WithoutDeleting()
    {
        string s = SbxSandbox.LayoutScript("/c/w", new[] { ("/c/proj", "/host/proj"), ("/c/it's", "/host/its") });
        Assert.StartsWith("mkdir -p /host && ", s);
        Assert.Contains("ln -sfn '/c/w' '/work'", s);
        Assert.Contains("ln -sfn '/c/proj' '/host/proj'", s);
        Assert.Contains("ln -sfn '/c/it'\\''s' '/host/its'", s);
        // Never deletes (a real dir at a link path may be host data on Linux/macOS) and never uses $.
        Assert.DoesNotContain("rm ", s);
        Assert.DoesNotContain("$", s);
    }

    [Fact]
    public void SbxLayoutScript_RefusesToShadowARealDirectory()
    {
        if (OperatingSystem.IsWindows()) return;   // runs the script under a real POSIX sh
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mux-layout-" + Guid.NewGuid().ToString("N")[..6]);
        string realDir = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "work")).FullName;
        System.IO.File.WriteAllText(System.IO.Path.Combine(realDir, "keep.txt"), "host data");
        try
        {
            // Swap the fixed /work and /host targets for temp paths so the real script body runs safely.
            string script = SbxSandbox.LayoutScript("/tmp", Array.Empty<(string, string)>())
                .Replace("'/work'", "'" + realDir + "'").Replace("mkdir -p /host", "mkdir -p '" + root + "/host'");
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sh")
                { ArgumentList = { "-c", script }, RedirectStandardError = true })!;
            p.WaitForExit();
            Assert.NotEqual(0, p.ExitCode);
            Assert.Equal("host data", System.IO.File.ReadAllText(System.IO.Path.Combine(realDir, "keep.txt")));
        }
        finally { System.IO.Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("{\"sandboxes\":[{\"name\":\"mux-a-1\",\"status\":\"stopped\"}]}", "mux-a-1", true)]
    [InlineData("{\"sandboxes\":[]}", "mux-a-1", false)]
    [InlineData("not json", "mux-a-1", false)]
    public void SbxListContains_ParsesLsJson(string json, string name, bool expected) =>
        Assert.Equal(expected, SbxSandbox.ListContains(json, name));

    private static FilesystemConfig Fs(string mode, params string[] paths) =>
        new() { SecurityMode = mode, AllowedPaths = new List<string>(paths) };

    [Fact]
    public void Mounts_Standard_FirstRw_RestRo()
    {
        var m = SandboxBackend.ResolveMounts(Fs("standard", @"C:\proj", @"C:\refs", @"C:\data"));
        Assert.Equal(3, m.Count);
        Assert.False(m[0].ReadOnly);  // workspace (first) RW
        Assert.True(m[1].ReadOnly);   // rest RO
        Assert.True(m[2].ReadOnly);
        Assert.StartsWith("/host/", m[0].GuestPath);
    }

    [Fact]
    public void Mounts_Secure_AllReadOnly()
    {
        var m = SandboxBackend.ResolveMounts(Fs("secure", @"C:\proj", @"C:\refs"));
        Assert.All(m, x => Assert.True(x.ReadOnly));
    }

    [Fact]
    public void Mounts_LaxAndNone_AllReadWrite()
    {
        foreach (var mode in new[] { "lax", "yolo", "none" })
        {
            var m = SandboxBackend.ResolveMounts(Fs(mode, @"C:\proj", @"C:\refs"));
            Assert.All(m, x => Assert.False(x.ReadOnly));
        }
    }

    [Fact]
    public void Mounts_Empty_WhenNoAllowedPaths()
    {
        Assert.Empty(SandboxBackend.ResolveMounts(Fs("standard")));
    }

    [Fact]
    public void Mounts_DedupGuestLeaves()
    {
        // Two different host paths sharing a leaf name must get distinct guest mount points.
        var m = SandboxBackend.ResolveMounts(Fs("none", @"C:\a\proj", @"D:\b\proj"));
        Assert.Equal(2, m.Count);
        Assert.NotEqual(m[0].GuestPath, m[1].GuestPath);
    }

    [Fact]
    public void UnknownBackend_ListsKata()
    {
        // The valid-backends hint must advertise kata so users discover the microVM option.
        var err = SandboxBackend.Validate(Cfg("garbage-backend"));
        Assert.NotNull(err);
        Assert.Contains("kata", err);
    }

    [Fact]
    public void Kata_IsOsAndKvmGated_NeverSilentFallback()
    {
        // kata is a microVM runtime: on non-Linux (or Linux without /dev/kvm) it MUST be a hard
        // validation error, never a silent fall-through to host or a weaker backend.
        var err = SandboxBackend.Validate(Cfg("kata"));
        if (OperatingSystem.IsWindows() || !System.IO.File.Exists("/dev/kvm"))
        {
            Assert.NotNull(err);
            // The failure must name kata/microVM/KVM so the reason is legible, not a generic miss.
            Assert.True(
                err!.Contains("kata", StringComparison.OrdinalIgnoreCase) ||
                err.Contains("microVM", StringComparison.OrdinalIgnoreCase) ||
                err.Contains("kvm", StringComparison.OrdinalIgnoreCase),
                $"kata gating error should explain the microVM/KVM requirement, got: {err}");
        }
    }

    [Fact]
    public void Kata_RejectsAllowlist_OnNonKvmHost()
    {
        // An allowlist is only meaningful once the backend resolves; on a host that can't run kata the
        // OS/KVM gate fires first and a kata config never validates. On a host that CAN run kata
        // (Linux + /dev/kvm, e.g. a nested-virt CI runner) the allowlist is legitimately valid on the
        // OCI-family kata backend, so only assert rejection where the microVM gate actually fires.
        if (OperatingSystem.IsWindows() || !System.IO.File.Exists("/dev/kvm"))
            Assert.NotNull(SandboxBackend.Validate(Cfg("kata", allow: new() { "pypi.org" })));
    }
}
