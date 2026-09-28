using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MuxSwarm.Engine.Proxy;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Slice 2/3 coverage for the CLIProxyAPI sidecar: config.yaml generation, port helpers, and a GATED
/// live spawn/health/auth-files smoke test (set MUX_CLIPROXY_LIVE=1 to run; it downloads the real binary
/// and spawns it). The pure helpers run offline in normal CI.
/// </summary>
public class CliProxyManagerTests
{
    [Fact]
    public void BuildConfigYaml_BindsLoopback_EnablesManagement_PinsKeysAndAuthDir()
    {
        string yaml = CliProxyManager.BuildConfigYaml(
            port: 49317, apiKey: "client-key-abc", mgmtKey: "mgmt-key-xyz",
            authDir: @"C:\Users\x\AppData\Local\Mux-Swarm\cliproxy\auth");

        Assert.Contains("host: \"127.0.0.1\"", yaml);
        Assert.Contains("port: 49317", yaml);
        Assert.Contains("- \"client-key-abc\"", yaml);          // client bearer in api-keys list
        Assert.Contains("secret-key: \"mgmt-key-xyz\"", yaml);   // management enabled (non-empty)
        Assert.Contains("allow-remote: false", yaml);
        // auth-dir path is normalized to forward slashes for YAML.
        Assert.Contains("auth-dir: \"C:/Users/x/AppData/Local/Mux-Swarm/cliproxy/auth\"", yaml);
        Assert.DoesNotContain("\\", yaml);
    }

    [Fact]
    public void PathsAreLocal_AndVersioned()
    {
        // Install dir is versioned (side-by-side installs) and follows the ACTIVE version - the pin, or a
        // `/proxy update` override recorded on this machine - so this holds on any host state.
        Assert.Contains(CliProxyManager.ActiveVersion, CliProxyManager.InstallDir);
        Assert.Contains(CliProxyAssets.Version, CliProxyManager.InstallDirFor(CliProxyAssets.Version));
        Assert.EndsWith("config.yaml", CliProxyManager.ConfigPath);
        Assert.EndsWith("auth", CliProxyManager.AuthDir);
        // Never on a NAS/UNC path (the no-exec-on-NAS rule); must be a local rooted path.
        Assert.False(CliProxyManager.InstallDir.StartsWith(@"\\"), "install dir must not be a UNC path");
    }

    [Fact]
    public void PreferredPort_IsInDynamicRange()
    {
        Assert.InRange(CliProxyManager.PreferredPort, 49152, 65535);
    }

    [Fact]
    public void NotRunning_ByDefault_EndpointsNull()
    {
        // In a fresh unit-test process the sidecar is not started.
        if (!CliProxyManager.IsRunning)
        {
            Assert.Null(CliProxyManager.BaseUrl);
            Assert.Null(CliProxyManager.OpenAiEndpoint);
        }
    }

    [Fact]
    public async Task GetAuthFiles_Throws_WhenNotRunning()
    {
        if (!CliProxyManager.IsRunning)
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => CliProxyManager.GetAuthFilesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task VerifyProviderReadyAfterLogin_WhenNotRunning_DoesNotThrow_AndDoesNotHangOrPunish()
    {
        // With no sidecar running, IsProviderReadyAsync's underlying mgmt call throws; the verifier must
        // swallow that ("cannot verify - do not punish the user with a recycle") and return true rather
        // than propagating. Bounded: must complete promptly without a live network dependency.
        if (CliProxyManager.IsRunning) return;   // skip if a real sidecar is up in this environment
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        bool ready = await CliProxyManager.VerifyProviderReadyAfterLoginAsync("claude", cts.Token);
        Assert.True(ready);
    }

    [Fact]
    public async Task Live_SpawnHealthAuthFiles_RoundTrips()
    {
        // Gated: only runs when explicitly opted-in (downloads the real binary + spawns it).
        if (Environment.GetEnvironmentVariable("MUX_CLIPROXY_LIVE") != "1")
            return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            string endpoint = await CliProxyManager.EnsureRunningAsync(cts.Token).ConfigureAwait(false);

            Assert.Equal($"http://127.0.0.1:{CliProxyManager.PreferredPort}/v1", endpoint);
            Assert.True(CliProxyManager.IsRunning);
            Assert.NotNull(CliProxyManager.ClientApiKey);
            // The client key is exposed via env var for the OpenAI provider path to resolve.
            Assert.Equal(CliProxyManager.ClientApiKey,
                Environment.GetEnvironmentVariable(CliProxyManager.ClientKeyEnvVar));

            // /v1/models is the health endpoint; auth-files is the provider-readiness probe.
            var files = await CliProxyManager.GetAuthFilesAsync(cts.Token).ConfigureAwait(false);
            Assert.NotNull(files); // empty on a fresh box (no providers logged in) — that's valid
            Assert.False(await CliProxyManager.IsProviderReadyAsync("claude", cts.Token).ConfigureAwait(false));

            // Detached-survival / adopt-by-port: simulate a NEW Mux session by clearing this process's
            // in-memory "running" state, then EnsureRunning again. It must ADOPT the still-listening
            // detached proxy on the same port without spawning a second one.
            CliProxyManager.ResetSessionStateForTests();
            Assert.False(CliProxyManager.IsRunning);
            string endpoint2 = await CliProxyManager.EnsureRunningAsync(cts.Token).ConfigureAwait(false);
            Assert.Equal(endpoint, endpoint2);           // same port, adopted
            Assert.True(CliProxyManager.IsRunning);
        }
        finally
        {
            CliProxyManager.Stop();
            // Give the OS a moment to release the port after the tree kill.
            await Task.Delay(1500).ConfigureAwait(false);
            Assert.False(CliProxyManager.IsRunning);
        }
    }

    [Fact]
    public async Task Live_Spawn_Survives_ProcessExit_Marker()
    {
        // Gated, and ONLY the spawn half: starts a detached sidecar and intentionally does NOT stop it, so
        // an external harness can verify it outlives this test-host process. A companion env var
        // MUX_CLIPROXY_NOSTOP=1 is required to actually leave it running.
        if (Environment.GetEnvironmentVariable("MUX_CLIPROXY_LIVE") != "1") return;
        if (Environment.GetEnvironmentVariable("MUX_CLIPROXY_NOSTOP") != "1") return;

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        string endpoint = await CliProxyManager.EnsureRunningAsync(cts.Token).ConfigureAwait(false);
        Assert.True(CliProxyManager.IsRunning);
        // Deliberately leave it running: the whole point is to prove detached survival.
    }

    [Fact]
    public void LoginProviders_CoverSubscriptionProviders_WithCorrectFlags()
    {
        var lp = CliProxyManager.LoginProviders;
        Assert.Equal("-claude-login", lp["claude"]);
        Assert.Equal("-codex-login", lp["codex"]);
        Assert.True(lp.ContainsKey("kimi"));
        Assert.True(lp.ContainsKey("xai"));
        Assert.True(lp.ContainsKey("antigravity"));
        // Case-insensitive lookup.
        Assert.Equal("-claude-login", lp["CLAUDE"]);
    }

    [Fact]
    public async Task LoginAsync_UnknownProvider_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => CliProxyManager.LoginAsync("not-a-provider", CancellationToken.None));
    }

    [Fact]
    public void PinnedVersion_MatchesAssetTable()
    {
        Assert.Equal(CliProxyAssets.Version, CliProxyManager.PinnedVersion);
    }

    [Fact]
    public void ClientKeyEnvVar_IsStable()
    {
        Assert.Equal("MUX_CLIPROXY_KEY", CliProxyManager.ClientKeyEnvVar);
    }

    [Fact]
    public void AcquirePreferredPort_ReturnsPreferred_WhenFree()
    {
        // Nothing squatting -> must pin to the config port, never drift.
        if (!CliProxyManager.PortIsFree(CliProxyManager.PreferredPort))
            return; // a real sidecar/other proc holds it in this env; skip (covered by the live test)

        Assert.Equal(CliProxyManager.PreferredPort, CliProxyManager.AcquirePreferredPort());
    }

    [Fact]
    public void AcquirePreferredPort_FallsBackToFreePort_WhenPreferredHeldByUnkillableListener()
    {
        // Occupy PreferredPort with a plain socket owned by THIS process. It is a raw TcpListener, not a
        // cli-proxy-api process the killer can resolve+terminate, so the reclaim must fail gracefully and
        // return a DIFFERENT free port rather than hang or hand back the occupied one. This exercises the
        // "could not reclaim -> use a free port (config is rewritten to match)" branch deterministically.
        if (!CliProxyManager.PortIsFree(CliProxyManager.PreferredPort))
            return; // port already busy in this env; skip

        var squatter = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback, CliProxyManager.PreferredPort);
        squatter.Start();
        try
        {
            int chosen = CliProxyManager.AcquirePreferredPort();
            Assert.NotEqual(CliProxyManager.PreferredPort, chosen);
            Assert.InRange(chosen, 1, 65535);
        }
        finally
        {
            squatter.Stop();
        }
    }

    [Fact]
    public void UnixSpawn_FallsBackWhenSetsidIsMissing_AndKeepsArgsPositional()
    {
        // macOS ships no `setsid` (util-linux); an unconditional `setsid` made sh exit 127 and the sidecar
        // never started. The launcher must probe for it and fall back to a job-control process group.
        var psi = CliProxyManager.BuildUnixSpawn("/opt/mux dir/cli-proxy-api", "/cfg dir/config.yaml", "/opt/mux dir");
        Assert.Equal("/bin/sh", psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(new[] { "-c", CliProxyManager.UnixLaunchScript, "/opt/mux dir/cli-proxy-api", "/cfg dir/config.yaml" },
                     psi.ArgumentList.ToArray());

        string s = CliProxyManager.UnixLaunchScript;
        Assert.Contains("command -v setsid", s);          // probe, never assume
        Assert.Contains("exec setsid", s);                // Linux: no /bin/sh left behind in Mux's group
        Assert.Contains("POSIX::setsid()", s);            // macOS: new session even without a TTY
        Assert.Contains("set -m 2>/dev/null", s);         // last resort; job-control warning never leaks
        Assert.Contains("trap '' HUP", s);                // survive terminal close
        Assert.Contains("&", s);                          // detached, shell returns immediately
        Assert.DoesNotContain("$2", s);                   // paths are only ever positional $0/$1
        Assert.Equal(3, CountOccurrences(s, "</dev/null >/dev/null 2>&1")); // stdio never inherits the TUI tty
    }

    [Fact]
    public void UnixSpawn_WithoutSetsid_PerlBranchStartsANewSession()
    {
        // Mux Bud #87 finding: without setsid and without a TTY (launchd, --daemon, CI) `set -m` is a no-op, so
        // the sidecar stayed in Mux's process group. Hide setsid from PATH, run the real script, and check that
        // the child reports a process group different from the test process's own. pgid, not sid: BSD/macOS
        // `ps` has no `sid` keyword, and setsid() also starts a new process group.
        if (OperatingSystem.IsWindows()) return;
        var tools = new[] { "perl", "ps", "sh", "sleep", "mv" }.Select(Which).ToArray();
        if (tools.Any(t => t == null)) return;                // minimal images: nothing to prove

        string dir = Directory.CreateTempSubdirectory("mux-setsid-").FullName;
        try
        {
            string bin = Path.Combine(dir, "bin");
            Directory.CreateDirectory(bin);
            foreach (var tool in tools)
                File.CreateSymbolicLink(Path.Combine(bin, Path.GetFileName(tool!)), tool!);

            string exe = Path.Combine(dir, "fake-exe"), outFile = Path.Combine(dir, "pgid.txt");
            File.WriteAllText(exe, "#!/bin/sh\nps -o pgid= -p $$ > \"$2.tmp\"; mv \"$2.tmp\" \"$2\"\nsleep 2\n");
            File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var psi = CliProxyManager.BuildUnixSpawn(exe, outFile, dir);
            psi.Environment["PATH"] = bin;                 // setsid NOT on PATH -> perl branch
            psi.RedirectStandardError = true;
            using (var p = Process.Start(psi)!)
            {
                Assert.True(p.WaitForExit(10_000), "launcher shell did not return");
                Assert.Equal("", p.StandardError.ReadToEnd()); // no job-control warning leaks
            }

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(outFile) && DateTime.UtcNow < deadline) Thread.Sleep(50);
            Assert.True(File.Exists(outFile), "sidecar stand-in never ran");
            int childPgid = int.Parse(File.ReadAllText(outFile).Trim());
            Assert.NotEqual(OwnProcessGroupId(tools[1]!), childPgid);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Theory]
    [InlineData("7.3.15", "7.2.44", true, "7.3.15")]   // user updated past the pin
    [InlineData("7.3.15", "7.4.0", true, "7.4.0")]     // later Mux bumped the pin past it: pin wins
    [InlineData("7.4.0", "7.4.0", true, "7.4.0")]
    [InlineData("7.3.15", "7.2.44", false, "7.2.44")]  // marker binary missing
    [InlineData("../x", "7.2.44", true, "7.2.44")]     // invalid marker
    [InlineData("", "7.2.44", true, "7.2.44")]
    public void ResolveActiveVersion_MarkerOnlyWinsWhenNewerAndInstalled(string marker, string pin, bool installed, string expected)
    {
        Assert.Equal(expected, CliProxyManager.ResolveActiveVersion(marker, pin, _ => installed));
    }

    private static string? Which(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':')
            .Select(d => Path.Combine(d, tool)).FirstOrDefault(File.Exists);

    private static int OwnProcessGroupId(string ps)
    {
        var psi = new ProcessStartInfo(ps) { RedirectStandardOutput = true };
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("pgid=");
        psi.ArgumentList.Add("-p"); psi.ArgumentList.Add(Environment.ProcessId.ToString());
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return int.Parse(o.Trim());
    }

    private static int CountOccurrences(string s, string sub)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { n++; i += sub.Length; }
        return n;
    }
}
