using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Utils;

/// <summary>
/// Size-tiered sub-agent context retention + surgical-query engine (SPEC v0.12.0).
///
/// A sub-agent's raw output is moved to the lead with a cost that scales to what the lead
/// actually needs, not how much was produced:
///   - small  (&lt;= ProgressEntryBudget)          -> inline raw (unchanged behaviour)
///   - medium (&lt;= 3 * ProgressEntryBudget)       -> LLM/extractive summary (today's CompactAsync)
///   - large  (&gt; spill threshold) OR lead near a -> SPILL raw to disk, return a short pointer
///            cumulative cap                          (status + 3-line headline + handle); the lead
///                                                     pulls detail on demand via read_delegation.
///
/// Spilled raw lives SANDBOX-FIRST (under filesystem.sandboxPath/delegations/&lt;scope&gt;/), falling
/// back to %LOCALAPPDATA%/Mux-Swarm/delegations/&lt;scope&gt;/ only when the sandbox is unwritable, and
/// to an inline summary as a last resort -- a delegation is NEVER failed by a retention IO error.
///
/// All tuning values SCALE off the three existing executionLimits budgets; the only new config key
/// is delegationRetentionDays (startup prune).
/// </summary>
internal static class DelegationStore
{
    internal record Retained(string Handle, string AgentName, string Path, int RawLen, string Status, string Summary);

    // Per-process registry of spilled delegations so read_delegation can resolve a handle even after
    // the orchestrator that produced it has unwound.
    private static readonly ConcurrentDictionary<string, Retained> _byHandle = new(StringComparer.OrdinalIgnoreCase);

    // Per-scope monotonically increasing sequence (handle suffix) and resolved root cache.
    private static readonly ConcurrentDictionary<string, int> _seq = new();
    private static readonly ConcurrentDictionary<string, string?> _rootCache = new();

    // Per-scope cumulative count of lead-facing chars injected via delegation tool results -- the
    // blowout gate (P3). Reset at session/goal start via ResetScope.
    private static readonly ConcurrentDictionary<string, int> _leadChars = new();

    // Ambient scope id (goalId / sessionTimestamp). Set once at each orchestrator entry; flows into
    // parallel children via AsyncLocal so every nested delegation shares the same retention scope.
    private static readonly AsyncLocal<string?> _scope = new();

    internal static string CurrentScope => _scope.Value is { Length: > 0 } s ? s : "session";

    internal static void SetScope(string? scopeId)
    {
        _scope.Value = string.IsNullOrWhiteSpace(scopeId) ? null : scopeId;
    }

    /// <summary>Reset the per-scope cumulative lead-char counter (call at session/goal start).</summary>
    internal static void ResetScope(string? scopeId = null)
    {
        var key = Sanitize(scopeId ?? CurrentScope);
        _leadChars[key] = 0;
    }

    // ── Derived tuning values (no new magic numbers — all scale off existing budgets) ──
    private static int ProgressEntryBudget => Math.Max(1, ExecutionLimits.Current.ProgressEntryBudget);
    /// <summary>A single result larger than this becomes a pointer (3x the per-entry budget).</summary>
    internal static int SpillThreshold => 3 * ProgressEntryBudget;
    /// <summary>Soft cumulative lead cap — past this, results are demoted to pointers.</summary>
    internal static int LeadSoftCap => Math.Max(1, ExecutionLimits.Current.ProgressLogTotalBudget);
    /// <summary>Hard cumulative lead cap — even a pointer past this collapses to a manifest stub.</summary>
    internal static int LeadHardCap => 2 * LeadSoftCap;
    /// <summary>Max chars a single read_delegation pull may return.</summary>
    internal static int ReadMaxChars => 2 * ProgressEntryBudget;

    // ── Scratch root resolution (sandbox-first, local fallback, null = inline) ──
    internal static string? RootFor(string scopeId)
    {
        var key = Sanitize(scopeId);
        return _rootCache.GetOrAdd(key, _ => ResolveRoot(key));
    }

    private static string? ResolveRoot(string sanitizedScope)
    {
        // 1) sandbox-first
        var sandbox = App.Config?.Filesystem?.SandboxPath;
        if (!string.IsNullOrWhiteSpace(sandbox))
        {
            var candidate = Path.Combine(sandbox, "delegations", sanitizedScope);
            if (TryEnsureWritable(candidate)) return candidate;
        }

        // 2) local fallback
        var local = Path.Combine(LocalDataRoot(), "delegations", sanitizedScope);
        if (TryEnsureWritable(local)) return local;

        // 3) neither writable -> caller uses inline summary
        return null;
    }

    private static bool TryEnsureWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write_probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string LocalDataRoot()
    {
        string root = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(root, "Mux-Swarm");
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sb.Length == 0 ? "session" : sb.ToString();
    }

    internal static string MakeHandle(string agentName, int seq) => $"d:{agentName}#{seq}";

    /// <summary>
    /// Best-effort write of a sub-agent's raw output to the scope's retention dir. Returns the
    /// Retained pointer (with handle) or null on any IO failure (caller falls back to inline).
    /// </summary>
    internal static Retained? Persist(string scopeId, string agentName, string rawResult,
        string? status, string? summary, string? artifacts)
    {
        var root = RootFor(scopeId);
        if (root is null) return null;

        var key = Sanitize(scopeId);
        int seq = _seq.AddOrUpdate(key, 1, (_, prev) => prev + 1);
        var handle = MakeHandle(agentName, seq);
        var fileName = $"{Sanitize(agentName)}-{seq:D3}.raw.md";
        var path = Path.Combine(root, fileName);

        try
        {
            var sb = new StringBuilder();
            sb.Append("---\n");
            sb.Append($"handle: {handle}\n");
            sb.Append($"agent: {agentName}\n");
            sb.Append($"status: {status ?? "unknown"}\n");
            sb.Append($"timestamp: {DateTime.UtcNow:O}\n");
            sb.Append($"rawLen: {rawResult.Length}\n");
            if (!string.IsNullOrWhiteSpace(summary)) sb.Append($"summary: {OneLine(summary)}\n");
            if (!string.IsNullOrWhiteSpace(artifacts)) sb.Append($"artifacts: {OneLine(artifacts)}\n");
            sb.Append("---\n\n");
            sb.Append(rawResult);
            File.WriteAllText(path, sb.ToString());
        }
        catch
        {
            return null;
        }

        var retained = new Retained(handle, agentName, path, rawResult.Length, status ?? "unknown", summary ?? "");
        _byHandle[handle] = retained;
        return retained;
    }

    internal static Retained? Resolve(string handle) =>
        _byHandle.TryGetValue(handle.Trim(), out var r) ? r : null;

    internal static IEnumerable<Retained> List(string scopeId)
    {
        var root = RootFor(scopeId);
        if (root is null) yield break;
        foreach (var r in _byHandle.Values)
            if (r.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                yield return r;
    }

    /// <summary>
    /// Surgical read of a spilled raw result. With a pattern: grep-like (regex, substring fallback)
    /// matches plus a small context window. Without: head/tail/whole, bounded by maxChars.
    /// </summary>
    internal static string ReadSlice(string handle, string? pattern, int? headLines, int? tailLines, int maxChars)
    {
        var r = Resolve(handle);
        if (r is null) return $"[read_delegation] handle not found: {handle}. Use the exact handle from the delegation pointer (e.g. d:WebAgent#3).";

        string raw;
        try { raw = File.ReadAllText(r.Path); }
        catch (Exception ex) { return $"[read_delegation] could not read spilled file for {handle}: {ex.Message}"; }

        // Strip the front-matter header so the model sees just the raw content.
        raw = StripFrontMatter(raw);
        var lines = raw.Replace("\r\n", "\n").Split('\n');

        if (!string.IsNullOrWhiteSpace(pattern))
        {
            Regex? rx = null;
            try { rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
            catch { rx = null; } // invalid regex -> substring fallback below

            var matched = new List<string>();
            var emitted = new HashSet<int>();
            const int ctx = 2;
            for (int i = 0; i < lines.Length; i++)
            {
                bool hit = rx is not null ? rx.IsMatch(lines[i])
                                          : lines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase);
                if (!hit) continue;
                for (int j = Math.Max(0, i - ctx); j <= Math.Min(lines.Length - 1, i + ctx); j++)
                {
                    if (emitted.Add(j))
                        matched.Add($"{j + 1,5}: {lines[j]}");
                }
                matched.Add("  ---");
            }
            if (matched.Count == 0)
                return $"[read_delegation] no match for /{pattern}/ in {handle} ({r.RawLen} chars). Try a broader pattern or omit it for head/tail.";
            return Bound(string.Join("\n", matched), maxChars);
        }

        if (headLines is int h && h > 0)
            return Bound(string.Join("\n", lines.Take(h)), maxChars);
        if (tailLines is int t && t > 0)
            return Bound(string.Join("\n", lines.Skip(Math.Max(0, lines.Length - t))), maxChars);

        // Whole, bounded.
        return Bound(raw, maxChars);
    }

    private static string StripFrontMatter(string raw)
    {
        if (!raw.StartsWith("---")) return raw;
        int idx = raw.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (idx < 0) return raw;
        int after = raw.IndexOf('\n', idx + 1);
        return after < 0 ? raw : raw[(after + 1)..].TrimStart('\n');
    }

    private static string Bound(string s, int maxChars)
    {
        if (s.Length <= maxChars) return s;
        return s[..maxChars] + $"\n... [truncated at {maxChars} chars — narrow with a pattern or head/tail]";
    }

    private static string OneLine(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static string Headline(string raw, string? summary, int lines = 3)
    {
        if (!string.IsNullOrWhiteSpace(summary)) return OneLine(summary);
        var firstLines = raw.Replace("\r\n", "\n").Split('\n')
            .Where(l => !string.IsNullOrWhiteSpace(l)).Take(lines);
        return OneLine(string.Join(" ", firstLines));
    }

    /// <summary>
    /// The tier engine: given a sub-agent's raw result, decide the lead-facing posture and return
    /// (leadFacing, retained). scopeId = goalId ?? sessionTimestamp. Defaults preserve current
    /// behaviour: small results inline unchanged, medium summarize via CompactAsync exactly as
    /// today; only LARGE results (or a lead near its cumulative cap) become pointers.
    /// </summary>
    internal static async Task<(string LeadFacing, Retained? Retained)> TierResultAsync(
        string scopeId,
        string agentName,
        string rawResult,
        string? status,
        string? summary,
        string? artifacts,
        IChatClient? compactionClient,
        ChatOptions? chatOptions)
    {
        rawResult ??= "";
        var scopeKey = Sanitize(scopeId);
        int used = _leadChars.GetValueOrDefault(scopeKey, 0);

        int peb = ProgressEntryBudget;
        bool small = rawResult.Length <= peb;
        bool large = rawResult.Length > SpillThreshold;

        // Build the would-be inline/summary candidate so we can size it against the cumulative cap.
        async Task<string> BuildSummaryAsync() => await ResultCompactor.CompactAsync(
            rawResult, completionStatus: status, completionSummary: summary,
            completionArtifacts: artifacts, charBudget: peb,
            chatClient: compactionClient, chatOptions: chatOptions);

        string candidate;
        string posture;
        if (small)
        {
            candidate = string.IsNullOrWhiteSpace(rawResult)
                ? $"[{agentName} completed but returned no output]"
                : rawResult;
            posture = "inline";
        }
        else if (!large)
        {
            candidate = await BuildSummaryAsync();
            posture = "summary";
        }
        else
        {
            candidate = ""; // forced pointer below
            posture = "pointer";
        }

        // Blowout gate: demote inline/summary to a pointer if it would push the lead past the soft cap.
        bool demote = posture != "pointer" && used + candidate.Length > LeadSoftCap;
        if (large || demote) posture = "pointer";

        if (posture != "pointer")
        {
            Emit(agentName, rawResult.Length, posture, null);
            _leadChars[scopeKey] = used + candidate.Length;
            return (candidate, null);
        }

        // ── Pointer posture: spill raw, return a short pointer ──
        var retained = Persist(scopeId, agentName, rawResult, status, summary, artifacts);
        if (retained is null)
        {
            // Retention unavailable (sandbox + local both unwritable): never fail the delegation —
            // fall back to an inline summary.
            var fallback = await BuildSummaryAsync();
            Emit(agentName, rawResult.Length, "summary-fallback", null);
            _leadChars[scopeKey] = used + fallback.Length;
            return (fallback, null);
        }

        var head = Headline(rawResult, summary);
        var pointer =
            $"[{agentName} done · {status ?? "success"}] {head}\n" +
            $"[spilled raw: {retained.RawLen} chars · handle {retained.Handle} · read with read_delegation]";

        // Hard cap: if even the pointer would cross the hard cap, collapse to a minimal stub and
        // record the full pointer in a per-scope manifest the lead can open via read_delegation.
        if (used + pointer.Length > LeadHardCap)
        {
            AppendManifest(scopeId, pointer);
            var stub = $"[{agentName} done · spilled · handle {retained.Handle}]";
            Emit(agentName, rawResult.Length, "pointer-stub", retained.Handle);
            _leadChars[scopeKey] = used + stub.Length;
            return (stub, retained);
        }

        Emit(agentName, rawResult.Length, "pointer", retained.Handle);
        _leadChars[scopeKey] = used + pointer.Length;
        return (pointer, retained);
    }

    private static void AppendManifest(string scopeId, string pointer)
    {
        var root = RootFor(scopeId);
        if (root is null) return;
        try { File.AppendAllText(Path.Combine(root, "manifest.md"), pointer + "\n\n"); }
        catch { /* best-effort */ }
    }

    private static void Emit(string agentName, int rawLen, string posture, string? handle)
    {
        // Structured-only: the web app / stdio integrations consume this event, but the TUI does NOT
        // surface a muted "[delegation] ... -> posture" line -- it added terminal noise (raw console
        // text that can't be collapsed) and exposes inner mechanics the user doesn't need. The user
        // just sees that delegation worked, not how it was tiered.
        MuxConsole.EmitDelegationCompacted(agentName, rawLen, posture, handle);
    }

    /// <summary>
    /// Startup prune of spilled-raw retention dirs older than delegationRetentionDays. Best-effort.
    /// </summary>
    internal static void PruneOldRetention(int retentionDays)
    {
        if (retentionDays <= 0) return;
        var roots = new List<string>();
        var sandbox = App.Config?.Filesystem?.SandboxPath;
        if (!string.IsNullOrWhiteSpace(sandbox)) roots.Add(Path.Combine(sandbox, "delegations"));
        roots.Add(Path.Combine(LocalDataRoot(), "delegations"));

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                            Directory.Delete(dir, recursive: true);
                    }
                    catch { /* skip locked/in-use */ }
                }
            }
            catch { /* best-effort */ }
        }
    }
}
