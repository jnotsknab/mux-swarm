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
    public void SbxMounts_DropNetworkSharesAndMissingPaths_KeepTheRest()
    {
        // sbx prompts "(y/N)" for a workspace that does not exist; a missing allowed path must never reach it.
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mux-sbxm-" + Guid.NewGuid().ToString("N")[..6]);
        string proj = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "proj")).FullName;
        string refs = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "refs")).FullName;
        string gone = System.IO.Path.Combine(root, "gone");
        try
        {
            var m = SandboxBackend.SbxMounts(Fs("standard", proj, @"\\nas\share\docs", "//nas/share/x", gone, refs));
            Assert.Equal(new[] { proj, refs }, m.Select(x => x.HostPath));
            Assert.False(m[0].ReadOnly);   // workspace posture is unchanged by the filter
            Assert.True(m[1].ReadOnly);
        }
        finally { System.IO.Directory.Delete(root, true); }
    }

    [Fact]
    public void Run_NeverLetsTheChildWaitOnStdin()
    {
        // A CLI that asks a question and reads stdin (sbx: "workspace does not exist, create it? (y/N)")
        // must see EOF at once. With stdin inherited it blocked on the user's console until the timeout,
        // which froze the first tool call of every docker session. Note: under `dotnet test` the host's own
        // stdin is usually not a console, so this pins the contract (EOF, fast) rather than reproducing the
        // console hang; the console repro is in the PR evidence.
        var (file, args) = OperatingSystem.IsWindows()
            ? ("powershell", "-NoProfile -Command \"$l = [Console]::In.ReadLine(); if ($null -eq $l) { 'eof' } else { 'got:' + $l }\"")
            : ("/bin/sh", "-c \"read l && echo got:$l || echo eof\"");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (ok, outp, err) = OciSandbox.Run(file, args, allowFail: true, timeoutMs: 20_000);
        Assert.True(ok, err);
        Assert.Contains("eof", outp);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");
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
        string closed = SbxSandbox.CreateArgs("mux-x-1", "python:3.12-slim", false, @"C:\a", @"C:\w", mounts);
        Assert.Equal("create --name mux-x-1 -q --pull missing -t python:3.12-slim --deny-network \"**\" shell \"C:\\a\" \"C:\\w\" \"C:\\proj\" \"C:\\refs:ro\"", closed);
        string open = SbxSandbox.CreateArgs("mux-x-1", "python:3.12-slim", true, @"C:\a", @"C:\w", mounts);
        Assert.DoesNotContain("--deny-network", open);
    }

    [Fact]
    public void SbxCreateArgs_ReadOnlyParentNeverFollowsAWritableChild()
    {
        // sbx applies workspaces in order and the later one wins for the paths it covers. Jonathan's config:
        // sandboxPath (rw) under the home dir (ro reference path), work dir also under home. Listed child-first,
        // `C:\Users\me:ro` made /work and the workspace read-only. Order must be shallowest first.
        var mounts = new[]
        {
            new SandboxMount(@"C:\Users\me\build\ws", "/host/ws", false),
            new SandboxMount(@"C:\Users\me", "/host/me", true),
            new SandboxMount(@"C:\Users\me\build\ws\Configs", "/host/Configs", true),
            new SandboxMount(@"C:\Users\me\Dev", "/host/Dev", true),
        };
        string a = SbxSandbox.CreateArgs("n", "img", false, @"C:\Users\me\AppData\repl\.sbx-anchor", @"C:\Users\me\AppData\repl\w", mounts);
        string ws = a[(a.IndexOf(" shell ", StringComparison.Ordinal) + 7)..];
        Assert.Equal("\"C:\\Users\\me\\AppData\\repl\\.sbx-anchor\" \"C:\\Users\\me:ro\" \"C:\\Users\\me\\Dev:ro\" " +
                     "\"C:\\Users\\me\\build\\ws\" \"C:\\Users\\me\\AppData\\repl\\w\" \"C:\\Users\\me\\build\\ws\\Configs:ro\"", ws);
    }

    [Theory]
    [InlineData(@"C:\a\b\", 3)]
    [InlineData("/home/me/x", 3)]
    [InlineData(@"C:\", 1)]
    public void SbxDepth_CountsSegments(string p, int d) => Assert.Equal(d, SbxSandbox.Depth(p));

    [Fact]
    public void SbxAnchor_IsASiblingOfTheWorkDir()
    {
        string w = System.IO.Path.Combine("base", "repl", "repl___primary__");
        Assert.Equal(System.IO.Path.Combine("base", "repl", ".sbx-anchor", "repl___primary__"), SbxSandbox.AnchorDir(w));
        // One per session: parallel sub-agents never share a primary workspace.
        Assert.NotEqual(SbxSandbox.AnchorDir(w), SbxSandbox.AnchorDir(System.IO.Path.Combine("base", "repl", "repl_sub1")));
    }

    [Theory]
    [InlineData("for d in /tmp /etc; do echo $d; done")]
    [InlineData("x=1; echo ${x}x \"$HOME\" 'single'")]
    [InlineData(@"echo a\b \""q\"" end\")]
    [InlineData("")]
    public void ShQuoteForArgv_RoundTripsThroughTheCommandLine(string script)
    {
        // The guest shell must receive the script byte-for-byte. The old quoting escaped every $ and doubled
        // every backslash, so `$d` arrived as `\$d` and never expanded (seen in Jonathan's session).
        string cmdline = "x " + OciSandbox.ShQuoteForArgv(script);
        var argv = CommandLineSplit(cmdline);
        Assert.Equal(2, argv.Count);
        Assert.Equal(script, argv[1]);
    }

    // The documented .NET/MSVCRT argv parsing (what a spawned sbx/docker process sees), so the test is
    // OS-independent: 2N backslashes + quote -> N backslashes and a quote toggle; 2N+1 -> N and a literal quote.
    private static List<string> CommandLineSplit(string s)
    {
        var args = new List<string>(); var cur = new System.Text.StringBuilder(); bool inQ = false, any = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\')
            {
                int n = 0; while (i < s.Length && s[i] == '\\') { n++; i++; }
                if (i < s.Length && s[i] == '"') { cur.Append('\\', n / 2); if (n % 2 == 1) cur.Append('"'); else { inQ = !inQ; any = true; } }
                else { cur.Append('\\', n); i--; }
                continue;
            }
            if (c == '"') { inQ = !inQ; any = true; continue; }
            if (c == ' ' && !inQ) { if (cur.Length > 0 || any) { args.Add(cur.ToString()); cur.Clear(); any = false; } continue; }
            cur.Append(c);
        }
        if (cur.Length > 0 || any) args.Add(cur.ToString());
        return args;
    }

    // ---- wrapper argv (v0.15.2 #2): ProcessStartInfo.Arguments is split with .NET/MSVCRT rules on every
    // OS, so POSIX single-quoting broke every multi-word command under bwrap/firejail/sandbox-exec. The
    // wrapper must receive the inner argv byte-for-byte.

    private static SandboxSpec WrapperSpec(string backend, bool net = false) =>
        new() { Kind = SandboxKind.Wrapper, Backend = backend, Binary = backend, Image = "", NetworkOpen = net };

    [Theory]
    [InlineData("bwrap")]
    [InlineData("firejail")]
    [InlineData("sandbox-exec")]
    public void WrapShellCommand_Wrapper_PassesCommandAsOneShArgument(string backend)
    {
        foreach (var cmd in new[] { "echo 'quoted test'", "hostname", "printf \"%s\\n\" \"$HOME\" a\\b", "" })
        {
            var (file, args) = SandboxBackend.WrapShellCommand(WrapperSpec(backend), cmd, "/work/my dir");
            Assert.Equal(backend, file);
            var argv = CommandLineSplit(args);
            Assert.Equal(new[] { "/bin/sh", "-c", cmd }, argv.Skip(argv.Count - 3).ToArray());
        }
    }

    [Fact]
    public void WrapProcess_Bwrap_KeepsWorkDirWithSpacesAsOneToken_AndNetworkFlag()
    {
        string wd = "/home/u/my work";
        var argv = CommandLineSplit(SandboxBackend.WrapProcess(WrapperSpec("bwrap"), new[] { "python", "-u", "worker.py" }, wd).Args);
        int i = argv.IndexOf("--bind");
        Assert.Equal(wd, argv[i + 1]);
        Assert.Equal(wd, argv[i + 2]);
        Assert.Equal(wd, argv[argv.IndexOf("--chdir") + 1]);
        Assert.DoesNotContain("--share-net", argv);
        Assert.Equal(new[] { "python", "-u", "worker.py" }, argv.Skip(argv.Count - 3).ToArray());
        Assert.Contains("--share-net", CommandLineSplit(SandboxBackend.WrapProcess(WrapperSpec("bwrap", net: true), new[] { "x" }, wd).Args));
    }

    [Fact]
    public void WrapProcess_Bwrap_DoesNotTieTheJobToTheSpawningThread()
    {
        // --die-with-parent = PR_SET_PDEATHSIG, which fires on spawning-THREAD exit: long jobs died with 137.
        Assert.DoesNotContain("--die-with-parent", CommandLineSplit(SandboxBackend.WrapProcess(WrapperSpec("bwrap"), new[] { "x" }, "/w").Args));
    }

    [Fact]
    public void WrapProcess_Firejail_WhitelistAndNetNone()
    {
        var argv = CommandLineSplit(SandboxBackend.WrapProcess(WrapperSpec("firejail"), new[] { "x" }, "/w d").Args);
        Assert.Contains("--whitelist=/w d", argv);
        Assert.Contains("--net=none", argv);
        Assert.DoesNotContain("--net=none", CommandLineSplit(SandboxBackend.WrapProcess(WrapperSpec("firejail", net: true), new[] { "x" }, "/w").Args));
    }

    [Fact]
    public void WrapProcess_SandboxExec_ProfileIsOneToken()
    {
        var argv = CommandLineSplit(SandboxBackend.WrapProcess(WrapperSpec("sandbox-exec"), new[] { "/bin/sh", "-c", "ls" }, "/Users/u/w").Args);
        Assert.Equal("-p", argv[0]);
        Assert.StartsWith("(version 1)(deny default)", argv[1]);
        Assert.Contains("(subpath \"/Users/u/w\")", argv[1]);
        Assert.DoesNotContain("(allow network*)", argv[1]);
        Assert.Equal(new[] { "/bin/sh", "-c", "ls" }, argv.Skip(2).ToArray());
    }

    [Fact]
    public void WrapProcess_RejectsNonWrapperSpecs()
    {
        var custom = new SandboxSpec { Kind = SandboxKind.Custom, Backend = "custom", Binary = "", Image = "", CustomTemplate = "{cmd}" };
        Assert.Throws<SandboxException>(() => SandboxBackend.WrapProcess(custom, new[] { "x" }, "/w"));
    }

    [Fact]
    public void WrapShellCommand_CustomOnUnix_PassesRenderedTemplateAsOneShArgument()
    {
        if (OperatingSystem.IsWindows()) return;   // Windows renders via cmd.exe /c (unchanged)
        var spec = new SandboxSpec { Kind = SandboxKind.Custom, Backend = "custom", Binary = "", Image = "img", CustomTemplate = "run {image} sh -c {cmd}" };
        var (file, args) = SandboxBackend.WrapShellCommand(spec, "echo 'a b'", "/w");
        Assert.Equal("/bin/sh", file);
        Assert.Equal(new[] { "-c", "run img sh -c echo 'a b'" }, CommandLineSplit(args).ToArray());
    }

    [Theory]
    [InlineData("Warning: an existing sandbox was detected. /bin/true will run without any additional sandboxing features", true)]
    [InlineData("", false)]
    [InlineData("Parent pid 12, child pid 13\nChild process initialized", false)]
    public void FirejailDegraded_DetectsUnconfinedFallback(string output, bool expected) =>
        Assert.Equal(expected, SandboxBackend.FirejailDegraded(output));

    // ---- REPL worker under wrapper/custom backends (v0.15.2 #1, security): the worker used to start on the
    // HOST for every non-container backend. It must now run as a wrapped process, or refuse (custom w/o opt-in).

    [Theory]
    [InlineData("bwrap")]
    [InlineData("firejail")]
    [InlineData("sandbox-exec")]
    public void ReplWorker_UnderWrapper_RunsThroughTheWrapper(string backend)
    {
        var (file, args) = ReplSession.WorkerCommand(WrapperSpec(backend), "/w d/.venv/bin/python", "/w d/worker.py", "/w d");
        Assert.Equal(backend, file);
        var argv = CommandLineSplit(args);
        Assert.Equal(new[] { "/w d/.venv/bin/python", "/w d/worker.py" }, argv.Skip(argv.Count - 2).ToArray());
    }

    [Fact]
    public void ReplWorker_OnHost_IsUnchanged()
    {
        var (file, args) = ReplSession.WorkerCommand(null, "python3", "/w/worker.py", "/w");
        Assert.Equal("python3", file);
        Assert.Equal("/w/worker.py", args);
    }

    [Fact]
    public void ReplWorker_UnderCustomWithReplStdio_RunsThroughTheTemplate()
    {
        var spec = new SandboxSpec { Kind = SandboxKind.Custom, Backend = "custom", Binary = "", Image = "img",
            CustomTemplate = "wrap -i {cmd}", CustomReplStdio = true };
        var (file, args) = ReplSession.WorkerCommand(spec, "/v/python", "/w/worker.py", "/w");
        Assert.NotEqual("/v/python", file);
        Assert.Contains("wrap -i", args);
        Assert.Contains("/w/worker.py", args);
    }

    [Fact]
    public void Custom_ReplStdio_FlowsFromConfigToSpec()
    {
        var on = SandboxBackend.Resolve(new SandboxConfig { Backend = "custom", Command = "x {cmd}", ReplStdio = true });
        var off = SandboxBackend.Resolve(new SandboxConfig { Backend = "custom", Command = "x {cmd}" });
        Assert.True(on!.CustomReplStdio);
        Assert.False(off!.CustomReplStdio);
    }

    [Fact]
    public void WrapProcess_ExposesReadOnlyPaths_ForFirejailAndSeatbelt()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('/');
        string py = home + "/.local/share/uv/python/cpython-3.12";
        var fj = CommandLineSplit(SandboxBackend.WrapProcess(WrapperSpec("firejail"), new[] { "x" }, "/w", new[] { py }).Args);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Contains("--whitelist=" + py, fj);
            Assert.Contains("--read-only=" + py, fj);
        }
        var sb = CommandLineSplit(SandboxBackend.WrapProcess(WrapperSpec("sandbox-exec"), new[] { "x" }, "/w", new[] { "/opt/homebrew/py" }).Args);
        Assert.Contains("(subpath \"/opt/homebrew/py\")", sb[1]);
        Assert.DoesNotContain("file-write* (subpath \"/opt/homebrew/py\")", sb[1]);
    }

    // Review #100 P0: pyvenv.cfg sits in the sandbox's WRITABLE work dir, so its `home` is attacker-controlled.
    // It may only ever name an existing directory under a host-derived Python root. (POSIX paths: wrapper
    // backends are Linux/macOS only, and the validation is POSIX-path based.)
    private static (string venvPython, string root, string prefix, Action cleanup) TamperFixture()
    {
        string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "muxvenv_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tmp);
        // The fixture is compared with RESOLVED paths, so resolve the temp dir once (macOS: /var -> /private/var).
        tmp = OperatingSystem.IsWindows() ? tmp : ReplSession.RealPath(tmp)!;
        string root = System.IO.Path.Combine(tmp, "uvpython");
        string prefix = System.IO.Path.Combine(root, "cpython-3.12");
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(prefix, "bin"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(prefix, "bin", "python3"), "");   // a real install prefix
        string venv = System.IO.Path.Combine(tmp, "work", ".venv");
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(venv, "bin"));
        return (System.IO.Path.Combine(venv, "bin", "python"), root, prefix, () => System.IO.Directory.Delete(tmp, true));
    }

    private static void WriteHome(string venvPython, string home) =>
        System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(venvPython))!, "pyvenv.cfg"),
            $"home = {home}\nversion = 3.12\n");

    [Fact]
    public void PythonReadPaths_TrustedPrefix_IsExposed()
    {
        if (OperatingSystem.IsWindows()) return;
        var (py, root, prefix, cleanup) = TamperFixture();
        try
        {
            WriteHome(py, prefix + "/bin");
            Assert.Equal(new[] { prefix }, ReplSession.PythonReadPaths(py, new[] { root }));
            Assert.Empty(ReplSession.PythonReadPaths("/nonexistent/bin/python", new[] { root }));
        }
        finally { cleanup(); }
    }

    [Theory]
    [InlineData("HOME")]          // the attack: whitelist all of ~ under firejail / Seatbelt
    [InlineData("OUTSIDE")]       // an existing dir outside every trusted root
    [InlineData("DOTDOT")]        // escapes the root via ..
    [InlineData("QUOTE")]         // SBPL string-literal injection
    [InlineData("MISSING")]       // under the root but does not exist
    [InlineData("NOINTERP")]      // under the root, exists, but holds no interpreter
    [InlineData("RELATIVE")]
    public void PythonReadPaths_TamperedPyvenvCfg_ExposesNothing(string kind)
    {
        if (OperatingSystem.IsWindows()) return;
        var (py, root, prefix, cleanup) = TamperFixture();
        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string value = kind switch
            {
                "HOME" => home + "/bin",      // prefix = ~ itself, which exists: rejected by the root check
                "OUTSIDE" => System.IO.Path.GetTempPath().TrimEnd('/') + "/bin",
                "DOTDOT" => prefix + "/../../../etc/bin",
                "QUOTE" => root + "/a\"))(allow default)(x/bin",
                "MISSING" => root + "/nope/bin",
                "NOINTERP" => root + "/data/bin",   // exists under the root but is not a Python install (e.g. homebrew/var)
                _ => "rel/cpython/bin",
            };
            if (kind == "NOINTERP") System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "data", "bin"));
            WriteHome(py, value);
            Assert.Empty(ReplSession.PythonReadPaths(py, new[] { root }));
        }
        finally { cleanup(); }
    }

    // Review #100 round 2: sandbox-exec matches RESOLVED paths and Homebrew prefixes are symlinks into Cellar,
    // so the exposed path must be the real one, and a link inside a root must not reach outside it.
    [Fact]
    public void PythonReadPaths_SymlinkedPrefix_ExposesTheRealPath_AndALinkOutOfTheRootIsRejected()
    {
        if (OperatingSystem.IsWindows()) return;
        var (py, root, prefix, cleanup) = TamperFixture();
        try
        {
            string link = System.IO.Path.Combine(root, "opt-python");
            System.IO.Directory.CreateSymbolicLink(link, prefix);
            WriteHome(py, link + "/bin");
            Assert.Equal(new[] { prefix }, ReplSession.PythonReadPaths(py, new[] { root }));

            // A link under the root that points OUTSIDE it (at a dir with an interpreter) is not trusted.
            string outside = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(root)!, "elsewhere");
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(outside, "bin"));
            System.IO.File.WriteAllText(System.IO.Path.Combine(outside, "bin", "python3"), "");
            string escape = System.IO.Path.Combine(root, "escape");
            System.IO.Directory.CreateSymbolicLink(escape, outside);
            WriteHome(py, escape + "/bin");
            Assert.Empty(ReplSession.PythonReadPaths(py, new[] { root }));
        }
        finally { cleanup(); }
    }

    // Review #100 round 3 (P0): link targets are sandbox-controlled, so RealPath must not recurse forever. Mux Bud's
    // repro: t -> real/deep, s -> t/../s/q (the kernel resolves s to real/s/q; a naive resolver loops on s).
    [Fact]
    public void RealPath_SandboxCraftedLinks_ResolveLikeTheKernel_AndLoopsReturnNull()
    {
        if (OperatingSystem.IsWindows()) return;
        var (py, root, prefix, cleanup) = TamperFixture();
        try
        {
            string w = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(py))!)!;
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(w, "real", "deep"));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(w, "real", "s", "q"));
            System.IO.Directory.CreateSymbolicLink(System.IO.Path.Combine(w, "t"), "real/deep");
            System.IO.Directory.CreateSymbolicLink(System.IO.Path.Combine(w, "s"), "t/../s/q");
            Assert.Equal(System.IO.Path.Combine(w, "real", "s", "q"), ReplSession.RealPath(System.IO.Path.Combine(w, "s")));

            WriteHome(py, System.IO.Path.Combine(w, "s") + "/bin");
            Assert.Empty(ReplSession.PythonReadPaths(py, new[] { root }));   // survives, exposes nothing

            System.IO.File.CreateSymbolicLink(System.IO.Path.Combine(w, "a"), "b");
            System.IO.File.CreateSymbolicLink(System.IO.Path.Combine(w, "b"), "a");
            Assert.Null(ReplSession.RealPath(System.IO.Path.Combine(w, "a")));
            WriteHome(py, System.IO.Path.Combine(w, "a") + "/bin");
            Assert.Empty(ReplSession.PythonReadPaths(py, new[] { root }));
        }
        finally { cleanup(); }
    }

    [Theory]
    [InlineData("/ok/path", true)]
    [InlineData("/a\"))(allow default)", false)]
    [InlineData("/a\\b", false)]
    [InlineData("/a\nb", false)]
    [InlineData("", false)]
    public void IsSafeSandboxPath_RejectsSbplBreakers(string path, bool expected) =>
        Assert.Equal(expected, SandboxBackend.IsSafeSandboxPath(path));

    [Fact]
    public void WrapProcess_DropsUnsafeReadOnlyPaths_AndRefusesUnsafeWorkDir()
    {
        var sb = CommandLineSplit(SandboxBackend.WrapProcess(WrapperSpec("sandbox-exec"), new[] { "x" }, "/w",
            new[] { "/opt/homebrew/ok", "/a\"))(allow default)(" }).Args);
        Assert.Contains("(subpath \"/opt/homebrew/ok\")", sb[1]);
        Assert.DoesNotContain("allow default", sb[1]);
        Assert.Throws<SandboxException>(() => SandboxBackend.WrapProcess(WrapperSpec("sandbox-exec"), new[] { "x" }, "/w\"x", null));
    }

    // Review #100 P2: install_package's wrapped job must get the read-only paths (uv binary + base interpreter).
    [Fact]
    public void WrapShellCommand_PassesReadOnlyPaths_ToTheWrapper()
    {
        var sb = CommandLineSplit(SandboxBackend.WrapShellCommand(WrapperSpec("sandbox-exec"), "uv pip install x", "/w",
            new[] { "/opt/homebrew/bin" }).Args);
        Assert.Contains("(subpath \"/opt/homebrew/bin\")", sb[1]);
        Assert.Equal(new[] { "/bin/sh", "-c", "uv pip install x" }, sb.Skip(sb.Count - 3).ToArray());
    }

    [Fact]
    public void InstallReadPaths_IncludesTheHostUvDirectory()
    {
        if (OperatingSystem.IsWindows()) return;
        var (py, root, prefix, cleanup) = TamperFixture();
        try
        {
            var paths = ReplSession.InstallReadPaths(py, uvPath: "/home/u/.local/bin/uv");
            Assert.Contains("/home/u/.local/bin", paths);
        }
        finally { cleanup(); }
    }

    // Review #99: a probe that cannot run must fail closed (it used to return null, which read as "not degraded").
    [Fact]
    public void ProbeOutput_MissingBinary_Throws_InsteadOfReturningNull()
    {
        var ex = Assert.Throws<SandboxException>(() =>
            SandboxBackend.ProbeOutput("mux-no-such-binary-" + Guid.NewGuid().ToString("N"), "--version"));
        Assert.Contains("could not run", ex.Message);
    }

    [Fact]
    public void ProbeOutput_HungProbe_Throws_InsteadOfReturningNull()
    {
        var (file, args) = OperatingSystem.IsWindows()
            ? ("powershell", "-NoProfile -Command Start-Sleep -Seconds 10")
            : ("sleep", "10");
        var ex = Assert.Throws<SandboxException>(() => SandboxBackend.ProbeOutput(file, args, timeoutMs: 300));
        Assert.Contains("did not finish", ex.Message);
    }

    [Fact]
    public void ListAllowed_ShowsHowEachHostPathAppearsInTheSandbox()
    {
        var spec = new SandboxSpec
        {
            Kind = SandboxKind.Sbx, Backend = "docker", Binary = "sbx", Image = "img",
            Mounts = new[] { new SandboxMount(@"C:\ws", "/host/ws", false), new SandboxMount(@"C:\refs", "/host/refs", true) },
        };
        string m = FilesystemTools.SandboxMapping(spec, new[] { @"C:\ws", @"C:\refs", @"C:\gone" });
        Assert.Contains("/work", m);
        Assert.Contains(@"C:\ws  ->  /host/ws (rw)", m);
        Assert.Contains(@"C:\refs  ->  /host/refs (ro)", m);
        Assert.Contains(@"C:\gone  ->  not mounted in the sandbox", m);
        Assert.Equal("", FilesystemTools.SandboxMapping(null, new[] { @"C:\ws" }));   // host execution: unchanged
    }

    [Fact]
    public void SbxLayoutScript_LinksWorkAndHostMounts_WithoutDeleting()
    {
        string s = SbxSandbox.LayoutScript("/c/w", new[] { ("/c/proj", "/host/proj"), ("/c/it's", "/host/its") });
        Assert.StartsWith("mkdir -p /host && ", s);
        Assert.Contains("ln -sfn '/c/w' '/work'", s);
        Assert.Contains("ln -sfn '/c/proj' '/host/proj'", s);
        Assert.Contains("ln -sfn '/c/it'\\''s' '/host/its'", s);
        // Never deletes (a real dir at a link path may be host data on Linux/macOS).
        Assert.DoesNotContain("rm ", s);
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
