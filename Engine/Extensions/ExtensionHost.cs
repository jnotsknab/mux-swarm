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

    // Intercept rules of the processes a Restart stopped, until the new set is installed; null otherwise.
    private static volatile (string Name, IReadOnlyList<InterceptRule> Rules)[]? _restarting;

    /// <summary>While a <see cref="Restart"/> runs: the stopped processes' intercept rules. Tool calls in that
    /// window resolve them like a dead process (each rule's <c>onTimeout</c>), so a deny guard never lapses.</summary>
    internal static IReadOnlyList<(string Name, IReadOnlyList<InterceptRule> Rules)>? Restarting => _restarting;

    /// <summary>Test seam: runs inside <see cref="Restart"/> after the old processes stop, before new ones start.</summary>
    internal static Action? BeforeStart;

    /// <summary>
    /// Stop every running process, then start the trusted process extensions in
    /// <paramref name="extensions"/>. Returns problems (untrusted, failed to start).
    /// </summary>
    internal static IReadOnlyList<string> Restart(IReadOnlyList<ExtensionRegistry.Extension> extensions, ICollection<string> trusted)
    {
        _restarting = [.. _running.Where(p => p.Intercepts.Count > 0).Select(p => (p.Name, p.Intercepts))];
        try { return StartAll(extensions, trusted); }
        finally { _restarting = null; }
    }

    private static IReadOnlyList<string> StartAll(IReadOnlyList<ExtensionRegistry.Extension> extensions, ICollection<string> trusted)
    {
        StopAll();
        BeforeStart?.Invoke();
        var problems = new List<string>();
        var started = new List<ExtensionProcess>();
        foreach (var ext in extensions)
        {
            if (ext.Process is not { } spec) continue;
            if (!trusted.Contains(ext.Name))
            {
                problems.Add($"{ext.Name}: process not started; add \"{ext.Name}\" to extensions.trusted to allow it.");
                continue;
            }
            try { started.Add(ExtensionProcess.Start(ext.Name, ext.Dir, spec, ext.Events, ext.Intercepts)); }
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
