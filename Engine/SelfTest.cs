using System.Diagnostics;
using MuxSwarm.Engine.Proxy;

namespace MuxSwarm.Engine;

/// <summary>
/// Built-in end-to-end self-checks run by <c>--selftest [name,...]</c> against the real, published
/// binary. This is the CI harness pattern: the workflow generates dummy configs (so first-run setup is
/// bypassed), runs <c>MuxSwarm --selftest</c> on every OS in the build matrix, and gates on the exit code.
/// Each check exercises a real control flow (downloads, spawns, network) that unit tests mock out.
///
/// To add a check: add one <see cref="Check"/> entry to <see cref="All"/>. Checks print their own
/// progress, return normally on success, and throw on failure (the message becomes the FAIL reason).
/// </summary>
internal static class SelfTest
{
    /// <summary>A named end-to-end check. <paramref name="Run"/> throws to fail.</summary>
    public sealed record Check(string Name, string Description, Func<CancellationToken, Task> Run);

    /// <summary>Every registered check, in run order when none are named.</summary>
    public static readonly IReadOnlyList<Check> All =
    [
        new("proxy",
            "CLIProxyAPI sidecar: download+verify, detached spawn, /v1/models health, stop, port released",
            ProxyCheckAsync),
    ];

    /// <summary>Per-check wall-clock ceiling; a hung check fails instead of wedging CI.</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Resolve a comma-separated selector ("proxy", "proxy,foo", "all", or null/empty for all) to checks.
    /// Returns the unknown names separately so the caller can fail loudly instead of silently skipping.
    /// </summary>
    public static (List<Check> Checks, List<string> Unknown) Resolve(string? selector)
    {
        var names = (selector ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0 || names.Any(n => n.Equals("all", StringComparison.OrdinalIgnoreCase)))
            return (All.ToList(), []);

        var checks = new List<Check>();
        var unknown = new List<string>();
        foreach (var n in names)
        {
            var c = All.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (c is null) unknown.Add(n);
            else if (!checks.Contains(c)) checks.Add(c);
        }
        return (checks, unknown);
    }

    /// <summary>
    /// Run the selected checks, printing a PASS/FAIL line per check plus a summary. Returns the process
    /// exit code: 0 when every check passed, 1 on any failure or an unknown check name.
    /// </summary>
    public static async Task<int> RunAsync(string? selector)
    {
        var (checks, unknown) = Resolve(selector);
        if (unknown.Count > 0)
        {
            MuxConsole.WriteError($"[selftest] unknown check(s): {string.Join(", ", unknown)}. " +
                                  $"Available: {string.Join(", ", All.Select(c => c.Name))}");
            return 1;
        }

        int failed = 0;
        foreach (var check in checks)
        {
            MuxConsole.WriteInfo($"[selftest] {check.Name}: {check.Description}");
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = new CancellationTokenSource(CheckTimeout);
                await check.Run(cts.Token);
                MuxConsole.WriteSuccess($"[selftest] PASS {check.Name} ({sw.Elapsed.TotalSeconds:0.0}s)");
            }
            catch (Exception ex)
            {
                failed++;
                MuxConsole.WriteError($"[selftest] FAIL {check.Name} ({sw.Elapsed.TotalSeconds:0.0}s): {ex.Message}");
            }
        }

        string summary = $"[selftest] {checks.Count - failed}/{checks.Count} passed";
        if (failed == 0) MuxConsole.WriteSuccess(summary);
        else MuxConsole.WriteError(summary);
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// The CLIProxyAPI flow real subscription use depends on, minus the OAuth login CI cannot do:
    /// provision (download + SHA256 verify + extract), spawn detached through the per-OS launcher, answer
    /// /v1/models with the generated key, then stop and confirm the port is released. Refuses to run
    /// against the real per-user proxy home so it can never disturb a live sidecar.
    /// </summary>
    private static async Task ProxyCheckAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(CliProxyManager.HomeEnvVar)))
            throw new InvalidOperationException(
                $"refusing to touch the real proxy home; set {CliProxyManager.HomeEnvVar} to a scratch directory.");

        MuxConsole.WriteMuted($"  home    : {CliProxyManager.ConfigDir}");
        MuxConsole.WriteMuted($"  version : v{CliProxyManager.ActiveVersion} ({CliProxyAssets.CurrentRid() ?? "unsupported rid"})");

        string exe = await CliProxyManager.EnsureBinaryAsync(ct);
        MuxConsole.WriteMuted($"  binary  : {exe}");

        string endpoint = await CliProxyManager.EnsureRunningAsync(ct);
        MuxConsole.WriteMuted($"  endpoint: {endpoint}");

        // Independent re-probe with the generated bearer (EnsureRunningAsync already waited for health;
        // this asserts the endpoint Mux hands to the OpenAI client really answers).
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
        using (var req = new HttpRequestMessage(HttpMethod.Get, endpoint.TrimEnd('/') + "/models"))
        {
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {CliProxyManager.ClientApiKey}");
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"GET {endpoint}/models returned {(int)resp.StatusCode}.");
        }

        int port = new Uri(endpoint).Port;
        CliProxyManager.Stop();
        for (int i = 0; i < 50 && !CliProxyManager.PortIsFree(port); i++)
            await Task.Delay(100, ct);
        if (!CliProxyManager.PortIsFree(port))
            throw new InvalidOperationException($"sidecar still listening on {port} after Stop().");
    }
}
