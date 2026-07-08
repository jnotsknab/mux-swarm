using System.Diagnostics;
using System.Runtime.InteropServices;
using MuxSwarm.Utils;

namespace MuxSwarm.State;

/// <summary>
/// Restarts the running mux-swarm process in place: spawns a fresh copy of the installed binary that
/// waits for THIS process to fully exit (via <c>--relaunch-after &lt;pid&gt;</c>) before it binds any
/// serve port, then exits the current process. Used by <c>POST /api/restart</c> and by the self-updater
/// after a binary swap. Cross-platform: resolves the binary from <see cref="PlatformContext.BaseDirectory"/>
/// (never a shim), detaches the child so it outlives us, and re-passes the original argv.
/// </summary>
public static class Relauncher
{
    private const string RelaunchAfterFlag = "--relaunch-after";

    /// <summary>Original process argv, captured at startup so a relaunch reproduces the same invocation.</summary>
    public static string[] OriginalArgs { get; set; } = Array.Empty<string>();

    private static string BinaryName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "MuxSwarm.exe" : "MuxSwarm";

    /// <summary>
    /// If argv contains <c>--relaunch-after &lt;pid&gt;</c>, block until that PID has exited (bounded),
    /// so the successor never races the predecessor for the serve port / file locks. Returns the argv
    /// with the flag pair stripped. No-op (returns argv unchanged) when the flag is absent.
    /// </summary>
    public static string[] WaitForPredecessorAndStrip(string[] args)
    {
        var idx = Array.IndexOf(args, RelaunchAfterFlag);
        if (idx < 0 || idx + 1 >= args.Length) return args;

        if (int.TryParse(args[idx + 1], out var pid))
        {
            try
            {
                var prev = Process.GetProcessById(pid);
                // Bounded wait: predecessor teardown (Kestrel + MCP children) is normally sub-second.
                prev.WaitForExit(30_000);
            }
            catch (ArgumentException) { /* already gone -- nothing to wait for */ }
            catch (Exception ex) { MuxConsole.WriteWarning($"relaunch: could not wait on pid {pid}: {ex.Message}"); }
        }

        // Strip the flag + its value from argv.
        var list = new List<string>(args);
        list.RemoveAt(idx + 1);
        list.RemoveAt(idx);
        return list.ToArray();
    }

    /// <summary>
    /// Spawn the successor process (detached, waiting on our PID) and return true if it started. The
    /// CALLER is responsible for tearing down and exiting this process immediately afterwards. The
    /// successor's argv = OriginalArgs (with any prior <c>--relaunch-after</c> removed) + a fresh
    /// <c>--relaunch-after &lt;ourPid&gt;</c>.
    /// </summary>
    public static bool SpawnSuccessor()
    {
        try
        {
            var exe = Path.Combine(PlatformContext.BaseDirectory, BinaryName);
            if (!File.Exists(exe))
            {
                MuxConsole.WriteWarning($"relaunch: binary not found at {exe}");
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                WorkingDirectory = PlatformContext.BaseDirectory,
            };

            // Re-pass original args, minus any stale relaunch pair.
            var prior = OriginalArgs;
            var priorIdx = Array.IndexOf(prior, RelaunchAfterFlag);
            for (int i = 0; i < prior.Length; i++)
            {
                if (i == priorIdx || i == priorIdx + 1) continue;
                psi.ArgumentList.Add(prior[i]);
            }
            psi.ArgumentList.Add(RelaunchAfterFlag);
            psi.ArgumentList.Add(Environment.ProcessId.ToString());

            var child = Process.Start(psi);
            if (child == null)
            {
                MuxConsole.WriteWarning("relaunch: Process.Start returned null.");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            MuxConsole.WriteWarning($"relaunch: failed to spawn successor: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Full restart: spawn the successor, then tear down and exit this process. Does not return on
    /// success. When <paramref name="applyStagedBinary"/> is set the successor will pick up a staged
    /// <c>.new</c> binary on boot (handled in App startup); nothing to do here beyond spawning.
    /// </summary>
    public static void RestartNow(Action? gracefulTeardown = null)
    {
        var spawned = SpawnSuccessor();
        if (!spawned)
        {
            MuxConsole.WriteError("Restart failed: could not spawn a successor process. Staying up.");
            return;
        }
        try { gracefulTeardown?.Invoke(); } catch { /* best effort */ }
        HookWorker.Stop();
        ProcessCleanup.Instance.Shutdown();
        Environment.Exit(0);
    }
}
