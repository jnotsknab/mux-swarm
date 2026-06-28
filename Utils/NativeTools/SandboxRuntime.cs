namespace MuxSwarm.Utils.NativeTools;

/// <summary>
/// Authoritative, process-global view of whether a real execution sandbox is active. Resolved ONCE
/// from <see cref="App"/>.Config.Sandbox (the <c>sandbox</c> config block - the SAME source of truth the
/// native exec tools use via <see cref="SandboxBackend.Resolve"/>), and re-resolved on <c>/sandbox</c>
/// (and <c>/dockerexec</c>) swaps. This exists to stop the preamble (and anyone else) from claiming the
/// sandbox is ACTIVE - and pointing the model at /work + /host/* mounts - when execution actually runs
/// natively on the host.
///
/// Why not <c>App.Config.IsUsingDockerForExec</c>: that flag is a loose intent bool that can drift from
/// the resolved backend (e.g. left true while <c>sandbox.backend</c> is "host"). The tools never trust it
/// - they resolve the spec from the sandbox config block - so the preamble must use the same signal.
///
/// "Active" means the backend resolves to a real sandbox (OCI/wrapper/custom). host / empty / none, and
/// any backend that fails to resolve (unknown, missing binary, daemon down) => NOT active: there is no
/// container/work dir to advertise, and the tools surface the real error at call time.
/// </summary>
internal static class SandboxRuntime
{
    private static readonly object _gate = new();
    private static SandboxSpec? _spec;
    private static bool _resolved;

    /// <summary>The active resolved sandbox spec, or null when execution runs on the host.</summary>
    public static SandboxSpec? Active
    {
        get
        {
            lock (_gate)
            {
                if (!_resolved) ResolveNoLock();
                return _spec;
            }
        }
    }

    /// <summary>True when a real sandbox backend is active; false for host/empty/none/unresolvable.</summary>
    public static bool IsActive => Active is not null;

    /// <summary>
    /// Re-resolve from the current <see cref="App"/>.Config.Sandbox. Call on startup and after every
    /// <c>/sandbox</c> / <c>/dockerexec</c> swap so the global state tracks the live config. A resolve
    /// failure (invalid/unavailable backend) is treated as NOT active - never a silent ACTIVE claim.
    /// </summary>
    public static void Refresh()
    {
        lock (_gate) ResolveNoLock();
    }

    private static void ResolveNoLock()
    {
        try { _spec = SandboxBackend.Resolve(App.Config.Sandbox); }
        catch (SandboxException) { _spec = null; }
        _resolved = true;
    }
}
