using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MuxSwarm.Utils.Proxy;
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
        // Install dir is versioned (side-by-side pins); config + auth live under the shared cliproxy dir.
        Assert.Contains(CliProxyAssets.Version, CliProxyManager.InstallDir);
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
    public async Task Live_SpawnHealthAuthFiles_RoundTrips()
    {
        // Gated: only runs when explicitly opted-in (downloads the real binary + spawns it).
        if (Environment.GetEnvironmentVariable("MUX_CLIPROXY_LIVE") != "1")
            return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            string endpoint = await CliProxyManager.EnsureRunningAsync(cts.Token).ConfigureAwait(false);

            Assert.EndsWith("/v1", endpoint);
            Assert.True(CliProxyManager.IsRunning);
            Assert.NotNull(CliProxyManager.ClientApiKey);

            // /v1/models is the health endpoint; auth-files is the provider-readiness probe.
            var files = await CliProxyManager.GetAuthFilesAsync(cts.Token).ConfigureAwait(false);
            Assert.NotNull(files); // empty on a fresh box (no providers logged in) — that's valid

            // No provider should be "ready" before any login.
            Assert.False(await CliProxyManager.IsProviderReadyAsync("claude", cts.Token).ConfigureAwait(false));
        }
        finally
        {
            CliProxyManager.Stop();
            Assert.False(CliProxyManager.IsRunning);
        }
    }
}
