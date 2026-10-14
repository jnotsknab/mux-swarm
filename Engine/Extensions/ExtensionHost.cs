namespace MuxSwarm.Engine.Extensions;

/// <summary>
/// Owns the running extension processes. Only extensions that declare a <c>process</c> AND are
/// listed in <c>extensions.trusted</c> are started. Inert (one volatile read per event) when no
/// process runs.
/// </summary>
internal static class ExtensionHost
{
    // Replaced wholesale (never mutated), so readers just take the current reference.
    private static volatile ExtensionProcess[] _running = [];

    /// <summary>The running extension processes.</summary>
    internal static IReadOnlyList<ExtensionProcess> Running => _running;

    /// <summary>
    /// Stop every running process, then start the trusted process extensions in
    /// <paramref name="extensions"/>. Returns problems (untrusted, failed to start).
    /// </summary>
    /// <param name="unsandboxed">Trusted extensions exempted from the sandbox by config.</param>
    /// <param name="sandbox">The configured sandbox; null runs every process on the host.</param>
    internal static IReadOnlyList<string> Restart(IReadOnlyList<ExtensionRegistry.Extension> extensions, ICollection<string> trusted,
        ICollection<string>? unsandboxed = null, SandboxConfig? sandbox = null)
    {
        StopAll();
        NativeTools.SandboxSpec? spec = null;
        string? sandboxError = null;
        if (sandbox is not null)
        {
            try { spec = NativeTools.SandboxBackend.Resolve(sandbox); }
            catch (NativeTools.SandboxException ex) { sandboxError = ex.Message; }
        }
        var problems = new List<string>();
        var started = new List<ExtensionProcess>();
        foreach (var ext in extensions)
        {
            if (ext.Process is not { } procSpec) continue;
            if (!trusted.Contains(ext.Name))
            {
                problems.Add($"{ext.Name}: process not started; add \"{ext.Name}\" to extensions.trusted to allow it.");
                continue;
            }
            bool exempt = unsandboxed?.Contains(ext.Name) == true;
            if (!exempt && sandboxError is not null)
            {
                // Never fall back to the host when the configured sandbox is unusable.
                problems.Add($"{ext.Name}: process not started: the sandbox is not usable ({sandboxError}). " +
                             $"Fix sandbox.backend or add \"{ext.Name}\" to extensions.unsandboxed.");
                continue;
            }
            try { started.Add(ExtensionProcess.Start(ext.Name, ext.Dir, procSpec, ext.Events, exempt ? null : spec, ext.Intercepts)); }
            catch (Exception ex) { problems.Add($"{ext.Name}: process failed to start: {ex.Message}"); }
        }
        _running = [.. started];
        return problems;
    }

    /// <summary>Stop and dispose every running extension process.</summary>
    internal static void StopAll()
    {
        var running = _running;
        _running = [];
        foreach (var p in running) p.Dispose();
    }

    /// <summary>Forward a lifecycle event to every running extension subscribed to it. Never blocks.</summary>
    internal static void Publish(HookEvent e)
    {
        var running = _running;
        if (running.Length == 0) return;
        foreach (var p in running)
            if (p.Wants(e.Event)) p.PostEvent(e);
    }
}
