using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using MuxSwarm.State;
using System.Diagnostics;

namespace MuxSwarm.Utils;

public static class SingleAgentOrchestrator
{

    public static Common.AgentDefinition? AgentDef = null;

    /// <summary>
    /// Slash-anywhere hand-off: when the user runs a REPL-only command inside a session and
    /// confirms ending it, the session loop stores the command here and exits. The App.cs
    /// menu loop dispatches it once after the session returns, then clears it. Null otherwise.
    /// </summary>
    public static string? PendingReplCommand = null;

    private static readonly Dictionary<string, string> Models = Common.LoadAgentModels();
    //uncached tokens
    private static uint _cachedTokens;
    //total tokens in session (cached + uncached)
    private static uint _sessionTokens;
    // Cumulative authoritative input/output token counts for the live session (for /cost).
    // Snapped from each UsageContent checkpoint's running totals; cleared on /wipe.
    private static long _cumInputTokens;
    private static long _cumOutputTokens;

    private static bool _pendingCompaction;

    // Batch6: set after /undo or /retry rebuilds the provider session, so the next
    // turn replays the trimmed conversationHistory into the fresh session (one-shot).
    private static bool _pendingReseed;

    // Batch6: exposed read-only to /api/status for a web context-usage meter.
    internal static uint SessionTokens => _sessionTokens;
    internal static int AutoCompactThreshold { get; private set; } = 80_000;

    public static Common.AgentDefinition? GetCurrSingleAgentDef(bool fromCfg = false)
    {
        if (AgentDef != null && !fromCfg) return AgentDef;

        var paths = new[]
        {
            MultiAgentOrchestrator.SwarmConfPath,
            PlatformContext.SwarmPath
        };

        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var json = File.ReadAllText(path);
                var config = JsonSerializer.Deserialize<SwarmConfig>(json);
                if (config?.SingleAgent != null)
                {
                    var def = Common.ParseSingleAgentDefinition(config);
                    if (!fromCfg) AgentDef = def;
                    return def;
                }
            }
            catch (Exception ex)
            {
                MuxConsole.WriteWarning($"[AGENT] Failed to parse singleAgent from {path}: {ex.Message}");
            }
        }
        return null;
    }


    private static bool IsQuitCommand(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return true;

        var trimmed = input.Trim();

        return trimmed.Equals("/qc", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/qm", StringComparison.OrdinalIgnoreCase);
    }

    private static ModelOpts? GetSingleAgentModelOpts()
    {
        if (!File.Exists(MultiAgentOrchestrator.SwarmConfPath))
            return null;

        try
        {
            var json = File.ReadAllText(MultiAgentOrchestrator.SwarmConfPath);
            var swarm = JsonSerializer.Deserialize<SwarmConfig>(json);
            return swarm?.SingleAgent?.ModelOpts;
        }
        catch { return null; }
    }

    /// <summary>
    /// /tag &lt;text&gt; - attach a free-form tag to the live session. Persists the session now
    /// (so a dir exists), appends the tag to the sidecar (tags.muxtag - intentionally NOT a
    /// *.json file so the resume single-vs-swarm detector is unaffected), then offers to also
    /// record a one-line stub in MEMORY.md via a one-shot LLM rewrite (opt-in). TUI/interactive
    /// only path; safe no-op style on any failure.
    /// </summary>
    private static async Task HandleTagAsync(
        string metaCmd,
        string sessionTimestamp,
        AgentSession session,
        AIAgent agent,
        string resolvedModelId,
        Func<string, IChatClient>? chatClientFactory,
        CancellationToken ct)
    {
        var parts = metaCmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
        {
            MuxConsole.WriteMuted("Usage: /tag <text>  - label this session for easy resume/search.");
            return;
        }
        var tag = parts[1].Trim();

        // Ensure the session is persisted so its directory exists, then resolve it.
        string? sessionDir = Common.FindSessionDirectory(sessionTimestamp);
        if (sessionDir == null)
        {
            try
            {
                await Common.PersistChatSessionAsync(agent, session, sessionTimestamp);
                sessionDir = Common.FindSessionDirectory(sessionTimestamp);
            }
            catch { /* fall through to warning below */ }
        }

        if (sessionDir == null)
        {
            MuxConsole.WriteWarning("Could not resolve the session directory to write the tag.");
            return;
        }

        if (!SessionTags.Append(sessionDir, tag))
        {
            MuxConsole.WriteWarning("Failed to write the session tag.");
            return;
        }

        MuxConsole.WriteSuccess($"Tagged session {sessionTimestamp}: \"{tag}\"");

        // Opt-in: also record a durable one-line stub in MEMORY.md.
        bool alsoMemory = MuxConsole.Confirm("Also record this tag as a stub in MEMORY.md?", false);
        if (!alsoMemory) return;

        await WriteMemoryTagStubAsync(tag, sessionTimestamp, resolvedModelId, chatClientFactory, ct);
    }

    /// <summary>
    /// Append/merge a 1-2 line stub for a session tag into MEMORY.md under a "Session Tags"
    /// section. Writes a .bak first. SCOPED: only adds the stub line, never rewrites the file
    /// wholesale. After writing, respects the configured MEMORY.md char-cap (warn/force).
    /// </summary>
    private static async Task WriteMemoryTagStubAsync(
        string tag,
        string sessionTimestamp,
        string resolvedModelId,
        Func<string, IChatClient>? chatClientFactory,
        CancellationToken ct)
    {
        try
        {
            var memoryPath = Path.Combine(PlatformContext.ContextDirectory, ContextCap.MemoryFile);
            var utc = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var stub = $"- `{sessionTimestamp}` - {tag} (tagged {utc} UTC)";
            const string header = "## Session Tags";

            string content = File.Exists(memoryPath) ? await File.ReadAllTextAsync(memoryPath, ct) : "";

            // Back up before mutating.
            if (File.Exists(memoryPath))
            {
                try { File.Copy(memoryPath, memoryPath + ".bak", overwrite: true); } catch { }
            }

            string updated;
            int idx = content.IndexOf(header, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                // Insert the stub right after the header line.
                int lineEnd = content.IndexOf('\n', idx);
                if (lineEnd < 0) lineEnd = content.Length;
                updated = content[..lineEnd] + "\r\n" + stub + content[lineEnd..];
            }
            else
            {
                var sep = content.Length > 0 && !content.EndsWith("\r\n") ? "\r\n\r\n" : "\r\n";
                updated = content + sep + header + "\r\n" + stub + "\r\n";
            }

            Directory.CreateDirectory(PlatformContext.ContextDirectory);
            await File.WriteAllTextAsync(memoryPath, updated, ct);
            MuxConsole.WriteSuccess($"Recorded tag stub in {ContextCap.MemoryFile}.");

            // Respect the MEMORY.md char-cap (warn/force) now that we have grown the file.
            await ContextCap.CheckFileAsync(ContextCap.MemoryFile, chatClientFactory, resolvedModelId, ct);
        }
        catch (Exception ex)
        {
            MuxConsole.WriteWarning($"Failed to write MEMORY.md tag stub: {ex.Message}");
        }
    }

    /// <summary>
    /// /handoff [instruction|path.md] - generate a cold-resume handoff document using the ACTIVE
    /// session model. Default save path is the sandbox reports dir; if the first token ends in
    /// ".md" it is treated as an explicit path override and the rest is the steering instruction.
    /// Offers to record a one-line pointer stub in MEMORY.md afterward.
    /// </summary>
    private static async Task HandleHandoffAsync(
        string metaCmd,
        IChatClient? client,
        IReadOnlyList<ChatMessage> history,
        ChatOptions? chatOptions)
    {
        if (client is null)
        {
            MuxConsole.WriteWarning("No active session model available for /handoff.");
            return;
        }

        var parts = metaCmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var arg = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        string? instruction;
        string savePath;
        var ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");

        var firstToken = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (firstToken.Length > 0 && firstToken[0].EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            savePath = firstToken[0];
            instruction = firstToken.Length > 1 ? firstToken[1].Trim() : null;
        }
        else
        {
            var sandbox = App.Config.Filesystem?.SandboxPath;
            var reportsDir = string.IsNullOrWhiteSpace(sandbox)
                ? Path.Combine(PlatformContext.ContextDirectory, "reports")
                : Path.Combine(sandbox, "reports");
            savePath = Path.Combine(reportsDir, $"HANDOFF_session_{ts}.md");
            instruction = string.IsNullOrWhiteSpace(arg) ? null : arg;
        }

        string? content = null;
        await MuxConsole.WithSpinnerAsync("Generating session handoff", async () =>
        {
            content = await SessionHandoff.GenerateAsync(history, client, instruction, chatOptions);
        });

        if (string.IsNullOrWhiteSpace(content))
        {
            MuxConsole.WriteWarning("Handoff generation returned nothing.");
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(savePath, content);
            MuxConsole.WriteSuccess($"Handoff written to {savePath}");
        }
        catch (Exception ex)
        {
            MuxConsole.WriteWarning($"Failed to write handoff: {ex.Message}");
            return;
        }

        if (MuxConsole.Confirm("Record a pointer stub for this handoff in MEMORY.md?", false))
        {
            try
            {
                var memPath = Path.Combine(PlatformContext.ContextDirectory, ContextCap.MemoryFile);
                var utc = DateTime.UtcNow.ToString("yyyy-MM-dd");
                var stub = $"- `{ts}` handoff -> {savePath} (written {utc} UTC)";
                const string header = "## Session Handoffs";
                string existing = File.Exists(memPath) ? await File.ReadAllTextAsync(memPath) : string.Empty;
                string updated;
                int idx = existing.IndexOf(header, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    int lineEnd = existing.IndexOf('\n', idx);
                    if (lineEnd < 0) lineEnd = existing.Length;
                    updated = existing[..lineEnd] + "\r\n" + stub + existing[lineEnd..];
                }
                else
                {
                    var sep = existing.Length > 0 && !existing.EndsWith("\r\n") ? "\r\n\r\n" : "\r\n";
                    updated = existing + sep + header + "\r\n" + stub + "\r\n";
                }
                Directory.CreateDirectory(PlatformContext.ContextDirectory);
                await File.WriteAllTextAsync(memPath, updated);
                MuxConsole.WriteSuccess($"Recorded handoff stub in {ContextCap.MemoryFile}.");
            }
            catch (Exception ex)
            {
                MuxConsole.WriteWarning($"Failed to write MEMORY.md stub: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// /heal [deep] [instruction] and /reflect [deep] [instruction] - self-examination pass over
    /// the current session using the ACTIVE session model. Surfaces proposed BRAIN/MEMORY
    /// write-backs and writes only the ones the user selects (MultiSelect). "deep" prints a
    /// cost/latency disclaimer; the cross-session swarm consolidation is a follow-up - deep
    /// currently runs the same single-pass analysis with consolidation guidance.
    /// </summary>
    private static async Task HandleHealAsync(
        string metaCmd,
        IChatClient? client,
        IReadOnlyList<ChatMessage> history,
        string resolvedModelId,
        Func<string, IChatClient>? chatClientFactory,
        ChatOptions? chatOptions,
        CancellationToken ct)
    {
        if (client is null)
        {
            MuxConsole.WriteWarning("No active session model available for /heal.");
            return;
        }

        var parts = metaCmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool deep = false;
        var rest = new List<string>();
        for (int i = 1; i < parts.Length; i++)
        {
            if (!deep && parts[i].Equals("deep", StringComparison.OrdinalIgnoreCase))
                deep = true;
            else
                rest.Add(parts[i]);
        }
        var instruction = rest.Count > 0 ? string.Join(' ', rest) : null;

        if (deep)
            MuxConsole.WriteMuted("Deep reflect reviews the whole session and may take longer / use more tokens.");

        List<SelfHeal.Proposal> proposals = new();
        await MuxConsole.WithSpinnerAsync(deep ? "Deep reflecting on session" : "Reviewing session", async () =>
        {
            proposals = await SelfHeal.AnalyzeAsync(history, client, deep, instruction, chatOptions, ct);
        });

        if (proposals.Count == 0)
        {
            MuxConsole.WriteMuted("No memory write-backs proposed.");
            return;
        }

        var labels = proposals.Select(pr => pr.Label).ToList();
        var picked = MuxConsole.MultiSelect(
            "Select the memory write-backs to apply (space to toggle, enter to confirm):", labels);

        if (picked.Count == 0)
        {
            MuxConsole.WriteMuted("Nothing selected; no changes written.");
            return;
        }

        var accepted = proposals.Where(pr => picked.Contains(pr.Label)).ToList();
        await SelfHeal.ApplyAsync(accepted, chatClientFactory, resolvedModelId, ct);
        MuxConsole.WriteSuccess($"Applied {accepted.Count} memory write-back(s) to BRAIN/MEMORY.");
    }

    // /fix [symptom]: collect a live runtime snapshot and have the active model diagnose what is
    // wrong + propose ordered, copy-pasteable repair steps (favouring Mux's own /refresh, /proxy,
    // /reloadskills, /provider, /setup, /sandbox commands). Read-only: it diagnoses, never mutates.
    private static async Task HandleFixAsync(
        string metaCmd,
        IChatClient? client,
        ChatOptions? chatOptions,
        CancellationToken ct)
    {
        if (client is null)
        {
            MuxConsole.WriteWarning("No active session model available for /fix.");
            return;
        }

        var parts = metaCmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var symptom = parts.Length > 1 ? parts[1].Trim() : null;

        string diagnosis = "";
        await MuxConsole.WithSpinnerAsync("Diagnosing Mux runtime", async () =>
        {
            diagnosis = await SystemDiagnostics.DiagnoseAsync(symptom, client, chatOptions, ct);
        });

        if (string.IsNullOrWhiteSpace(diagnosis))
        {
            MuxConsole.WriteMuted("No diagnosis produced. Try /status, /tools, or /proxy status for raw state.");
            return;
        }

        MuxConsole.WritePanel("/fix - diagnosis & repair plan", diagnosis);
    }

    // /diff: show the working-tree git diff (staged fallback) through the collapsible diff renderer.
    private static async Task HandleDiffAsync(CancellationToken ct)
    {
        string root = PlatformContext.WorkspaceRoot;
        if (!Directory.Exists(Path.Combine(root, ".git")))
        {
            // Walk up a few levels in case the workspace root is a subdir of the repo.
            var probe = new DirectoryInfo(root);
            bool found = false;
            for (int i = 0; i < 6 && probe is not null; i++)
            {
                if (Directory.Exists(Path.Combine(probe.FullName, ".git"))) { root = probe.FullName; found = true; break; }
                probe = probe.Parent;
            }
            if (!found)
            {
                MuxConsole.WriteMuted("not a git repository");
                return;
            }
        }

        ShellCapture.Result res = await ShellCapture.RunAsync("git diff", root, 30, 200_000, ct);
        string diff = res.Stdout;
        if (string.IsNullOrWhiteSpace(diff))
        {
            var staged = await ShellCapture.RunAsync("git diff --staged", root, 30, 200_000, ct);
            diff = staged.Stdout;
        }

        if (string.IsNullOrWhiteSpace(diff))
        {
            MuxConsole.WriteMuted("No changes in the working tree.");
            return;
        }

        MuxConsole.RenderTuiDiff("git diff", diff);
        if (!MuxConsole.IsTui)
            MuxConsole.WritePanel("git diff", diff);
    }

    // /doctor: non-LLM health rollup. Prints the SystemDiagnostics snapshot plus a terse PASS/WARN
    // summary computed in C# (no model call - cheap and offline).
    private static void HandleDoctor()
    {
        var snapshot = SystemDiagnostics.BuildSnapshot();

        var warns = new List<string>();
        if (App.ActiveProvider is null)
            warns.Add("no active LLM provider (use /provider or /setup)");
        var configured = App.Config?.McpServers ?? new();
        foreach (var (name, cfg) in configured)
        {
            bool nativeMarker = string.Equals(cfg.Command, "native-runtime-tools", StringComparison.OrdinalIgnoreCase)
                || (cfg.Args?.Any(a => string.Equals(a, "native-runtime-tools", StringComparison.OrdinalIgnoreCase)) ?? false);
            if (nativeMarker || !cfg.Enabled) continue;
            if (!App.McpClients.ContainsKey(name))
                warns.Add($"MCP '{name}' NOT CONNECTED");
        }
        if (SkillLoader.GetSkillMetadata().Count == 0)
            warns.Add("no skills loaded");

        var sb = new System.Text.StringBuilder();
        sb.Append(snapshot.TrimEnd());
        sb.Append("\n\n## Health\n");
        if (warns.Count == 0)
            sb.Append("PASS - providers, MCP servers, skills, and sandbox all look healthy.");
        else
        {
            sb.Append($"WARN - {warns.Count} issue(s):\n");
            foreach (var w in warns) sb.Append("  - ").Append(w).Append('\n');
        }
        MuxConsole.WritePanel("/doctor - runtime health", sb.ToString().TrimEnd());
    }

    // /cost: session token usage + estimated $ for API providers. Subscription providers routed
    // through the CLIProxy sidecar are flat-rate -> show usage only, no dollars.
    private static void HandleCost(string resolvedModelId)
    {
        long inTok = _cumInputTokens;
        long outTok = _cumOutputTokens;
        long cached = _cachedTokens;
        long total = _sessionTokens;

        bool isSubscription = string.Equals(
            App.ActiveProvider?.ApiKeyEnvVar,
            MuxSwarm.Utils.Proxy.CliProxyManager.ClientKeyEnvVar,
            StringComparison.Ordinal);

        var sb = new System.Text.StringBuilder();
        sb.Append($"model: {(string.IsNullOrWhiteSpace(resolvedModelId) ? "(unknown)" : resolvedModelId)}\n");
        sb.Append($"input tokens:  {inTok:N0}\n");
        sb.Append($"output tokens: {outTok:N0}\n");
        sb.Append($"cached input:  {cached:N0}\n");
        sb.Append($"session total: {total:N0}\n");

        if (isSubscription)
        {
            sb.Append("\ncost: subscription provider (CLIProxy sidecar) - flat-rate plan, no per-token cost. Showing usage only.");
        }
        else if (ModelPricing.Estimate(resolvedModelId, inTok, outTok) is { } usd)
        {
            var price = ModelPricing.Lookup(resolvedModelId)!.Value;
            sb.Append($"\nrate: ${price.InputPer1M:0.##}/1M in, ${price.OutputPer1M:0.##}/1M out (list-price estimate)\n");
            sb.Append($"estimated cost: ${usd:0.0000}");
        }
        else
        {
            sb.Append("\ncost: unknown (no price for this model id; set a ModelPricing override). Showing usage only.");
        }
        MuxConsole.WritePanel("/cost - session usage", sb.ToString());
    }

    // /cost all + /tokens all: Claude-Code-style matrixed breakdown sourced from the in-process
    // CostLedger (the OTEL counters are write-only). Per model: system-prompt / tools / cached /
    // input / output / reasoning, session AND rolling-cumulative, plus tool-call + compaction
    // counts and a proportional ASCII bar. Real $ from ModelPricing where the provider has prices;
    // subscription/cliproxy providers render tokens-only. Bare /cost + /tokens are unchanged.
    private static void HandleCostBreakdown(string resolvedModelId)
    {
        var rows = CostLedger.Snapshot();
        if (rows.Count == 0)
        {
            MuxConsole.WriteInfo("No usage recorded yet this run. Take a turn, then try /cost all.");
            return;
        }

        bool isSubscription = string.Equals(
            App.ActiveProvider?.ApiKeyEnvVar,
            MuxSwarm.Utils.Proxy.CliProxyManager.ClientKeyEnvVar,
            StringComparison.Ordinal);

        var sb = new System.Text.StringBuilder();
        double grandSessUsd = 0;
        bool anyPrice = false;

        foreach (var r in rows)
        {
            sb.Append($"\nmodel: {r.Model}\n");

            // Matrix: category | session | rolling. system-prompt + tools are a static subset of input.
            sb.Append("  category        session        rolling\n");
            sb.Append($"  system prompt   {r.SysPromptTok,12:N0}   {r.SysPromptTok,12:N0}\n");
            sb.Append($"  tools (schema)  {r.ToolsTok,12:N0}   {r.ToolsTok,12:N0}\n");
            sb.Append($"  input           {r.SessInput,12:N0}   {r.RollInput,12:N0}\n");
            sb.Append($"  cached input    {r.SessCached,12:N0}   {r.RollCached,12:N0}\n");
            sb.Append($"  output          {r.SessOutput,12:N0}   {r.RollOutput,12:N0}\n");
            sb.Append($"  reasoning       {r.SessReasoning,12:N0}   {r.RollReasoning,12:N0}\n");
            sb.Append($"  total           {r.SessTotal,12:N0}   {r.RollTotal,12:N0}\n");
            sb.Append($"  tool calls      {r.SessToolCalls,12:N0}   {r.RollToolCalls,12:N0}\n");
            sb.Append($"  compactions     {r.SessCompactions,12:N0}   {r.RollCompactions,12:N0}\n");

            // Proportional bar of the session input/output/cached/reasoning split.
            long barTotal = r.SessInput + r.SessOutput + r.SessReasoning;
            if (barTotal > 0)
            {
                sb.Append("  split  ");
                sb.Append(Bar("in", r.SessInput, barTotal));
                sb.Append(Bar("out", r.SessOutput, barTotal));
                if (r.SessReasoning > 0) sb.Append(Bar("rsn", r.SessReasoning, barTotal));
                sb.Append('\n');
            }

            // Cost: per-model $ from list price (input+output), or tokens-only for subscription.
            if (isSubscription)
            {
                sb.Append("  cost: subscription provider (flat-rate) - tokens only, no $\n");
            }
            else if (ModelPricing.Estimate(r.Model, r.SessInput, r.SessOutput) is { } sessUsd)
            {
                anyPrice = true;
                grandSessUsd += sessUsd;
                var rollUsd = ModelPricing.Estimate(r.Model, r.RollInput, r.RollOutput) ?? 0;
                var price = ModelPricing.Lookup(r.Model)!.Value;
                sb.Append($"  rate: ${price.InputPer1M:0.##}/1M in, ${price.OutputPer1M:0.##}/1M out (list-price estimate)\n");
                sb.Append($"  cost: session ${sessUsd:0.0000}   rolling ${rollUsd:0.0000}\n");
            }
            else
            {
                sb.Append("  cost: unknown (no ModelPricing entry for this model id) - tokens only\n");
            }
        }

        if (!isSubscription && anyPrice && rows.Count > 1)
            sb.Append($"\ngrand total (session, priced models): ${grandSessUsd:0.0000}\n");

        MuxConsole.WritePanel("/cost all - per-model breakdown", sb.ToString().TrimEnd());
    }

    // Small fixed-width proportional bar segment for the /cost all split line.
    private static string Bar(string label, long value, long total)
    {
        if (total <= 0) return string.Empty;
        int filled = (int)Math.Round((double)value / total * 10);
        filled = Math.Clamp(filled, 0, 10);
        double pct = (double)value / total * 100.0;
        return $"{label}[{new string('#', filled)}{new string('.', 10 - filled)}]{pct,3:F0}% ";
    }

    // /init: scan the workspace and have the active model author an AGENTS.md at its root.
    private static async Task HandleInitAsync(IChatClient? client, ChatOptions? chatOptions, CancellationToken ct)
    {
        if (client is null)
        {
            MuxConsole.WriteWarning("No active session model available for /init.");
            return;
        }
        string root = PlatformContext.WorkspaceRoot;
        string savePath = Path.Combine(root, "AGENTS.md");
        if (File.Exists(savePath) &&
            !MuxConsole.Confirm($"AGENTS.md already exists at {savePath}. Overwrite?", false))
        {
            MuxConsole.WriteMuted("Left existing AGENTS.md unchanged.");
            return;
        }

        string? content = null;
        await MuxConsole.WithSpinnerAsync("Analyzing workspace", async () =>
        {
            content = await ProjectInit.GenerateAsync(root, client, chatOptions, ct);
        });

        if (string.IsNullOrWhiteSpace(content))
        {
            MuxConsole.WriteWarning("Project analysis returned nothing.");
            return;
        }
        try
        {
            await File.WriteAllTextAsync(savePath, content, ct);
            MuxConsole.WriteSuccess($"Project context written to {savePath}");
        }
        catch (Exception ex)
        {
            MuxConsole.WriteWarning($"Failed to write AGENTS.md: {ex.Message}");
        }
    }

    // /review: read-only AI review of the working-tree diff (active model, no edits).
    private static async Task HandleReviewAsync(IChatClient? client, ChatOptions? chatOptions, CancellationToken ct)
    {
        if (client is null)
        {
            MuxConsole.WriteWarning("No active session model available for /review.");
            return;
        }
        string root = PlatformContext.WorkspaceRoot;
        if (!Directory.Exists(Path.Combine(root, ".git")))
        {
            var probe = new DirectoryInfo(root);
            for (int i = 0; i < 6 && probe is not null; i++)
            {
                if (Directory.Exists(Path.Combine(probe.FullName, ".git"))) { root = probe.FullName; break; }
                probe = probe.Parent;
            }
        }

        var res = await ShellCapture.RunAsync("git diff", root, 30, 200_000, ct);
        string diff = res.Stdout;
        if (string.IsNullOrWhiteSpace(diff))
        {
            var staged = await ShellCapture.RunAsync("git diff --staged", root, 30, 200_000, ct);
            diff = staged.Stdout;
        }
        if (string.IsNullOrWhiteSpace(diff))
        {
            MuxConsole.WriteMuted("nothing to review (clean working tree)");
            return;
        }

        string findings = "";
        await MuxConsole.WithSpinnerAsync("Reviewing diff", async () =>
        {
            findings = await CodeReview.ReviewAsync(diff, client, chatOptions, ct);
        });
        if (string.IsNullOrWhiteSpace(findings))
        {
            MuxConsole.WriteMuted("Review produced no findings.");
            return;
        }
        MuxConsole.WritePanel("/review - diff findings", findings);
    }

    public static async Task ChatAgentAsync(
        IChatClient? client,
        CancellationToken cancellationToken,
        int maxIterations = 15,
        IList<McpClientTool>? mcpTools = null,
        bool showToolResultCalls = false,
        bool shouldPlan = false,
        string? systemPromptOverride = null,
        Func<string, IChatClient>? chatClientFactory = null,
        int? autoCompactTokenThreshold = 80_000,
        bool persistSession = true,
        string? incomingGoal = null,
        bool continuous = false,
        string? goalId = null,
        uint minDelaySeconds = 0,
        uint persistIntervalSeconds = 0,
        uint sessionRetention = 0,
        JsonElement? resumedSession = null,
        string? resumedSessionDir = null,
        bool allowSubAgents = false,
        bool allowParallelSubAgents = false,
        MuxSwarm.Utils.Teams.TeamScope? teamScope = null,
        InteractiveSession? interactiveHandle = null)
    {
        // The classic line renderer shows a titled banner + help line. In the live TUI the
        // session header card (below) plays that role, so the banner/rule are suppressed to
        // avoid a redundant double-header. Stdio/serve emit their own structured events.
        if (!MuxConsole.IsTui)
        {
            MuxConsole.WriteBanner(persistSession ? "AGENTIC CHAT INTERFACE" : "STATELESS AGENTIC CHAT INTERFACE");
            MuxConsole.WriteMuted("Type /qc to exit, /compact to compress context. Press [Esc] to cancel the current turn.");
        }

        // When launched as a team (teamScope != null) the lead drives this loop with the
        // resolved lead definition; off-team this is exactly today's single-agent def.
        var singleAgentDef = teamScope?.LeadDef ?? GetCurrSingleAgentDef();
        var delegationResults = new List<MultiAgentOrchestrator.DelegationResult>();
        var pDelegationResults = new List<ParallelSwarmOrchestrator.DelegationResult>();
        var retryRegistry = new Dictionary<string, ParallelSwarmOrchestrator.RetryState>();
        var semaphore = new SemaphoreSlim(App.MaxDegreeParallelism);

        if (singleAgentDef != null && singleAgentDef.CanDelegate && !allowSubAgents && !allowParallelSubAgents)
            MuxConsole.WriteWarning($"[AGENT] {singleAgentDef.Name} is configured with delegation capabilities, run /subagents (/sub) or /parasubagents (/psub) to enable delegation in single agent mode.");

        var resolvedModelId = "";
        using var sessionSpan = OtelTracer.GetSource().StartActivity("agent_session");

        try
        {
            var swarmJson = File.ReadAllText(MultiAgentOrchestrator.SwarmConfPath);
            var swarm = JsonSerializer.Deserialize<SwarmConfig>(swarmJson);

            var match = singleAgentDef != null
                ? swarm?.Agents?.FirstOrDefault(a =>
                    a.Name != null && a.Name.Equals(singleAgentDef.Name, StringComparison.OrdinalIgnoreCase))
                : null;

            HookWorker.Enqueue(new HookEvent
            {
                Event = "session_start",
                Agent = singleAgentDef?.Name,
                Summary = persistSession ? "agent" : "stateless",
                Text = incomingGoal,
                Timestamp = DateTimeOffset.UtcNow
            });

            resolvedModelId = match?.Model ?? swarm?.SingleAgent?.Model ?? "";

            sessionSpan?.SetTag("agent", singleAgentDef?.Name);
            sessionSpan?.SetTag("mode", persistSession ? "agent" : "stateless");
            sessionSpan?.SetTag("model", resolvedModelId);
            sessionSpan?.SetTag("goal_id", goalId);
            OtelMetrics.SessionsStarted.Add(1, new KeyValuePair<string, object?>("agent", singleAgentDef?.Name));

        }
        catch { /* fall through */ }

        IList<AITool> allTools = (mcpTools ?? Array.Empty<McpClientTool>()).Cast<AITool>().ToList();
        // Merge the native in-house tools (Filesystem + shell/REPL) into the PRE-FILTER pool so
        // the per-agent ToolFilter gates them exactly like MCP tools: an agent gets them only if
        // it lists "Filesystem"/"Shell" in its swarm.json mcpServers (or a matching toolPattern).
        foreach (var nativeTool in MuxSwarm.Utils.NativeTools.NativeToolRegistry.BuildPool(App.Config))
            allTools.Add(nativeTool);
        IList<AITool> filteredTools = singleAgentDef?.ToolFilter(allTools) ?? allTools;

        if (filteredTools.Count == 0)
            MuxConsole.WriteWarning($"{singleAgentDef?.Name ?? "Agent"} Matched 0 tools. Check mcpServers in swarm.json singleAgent block.");
        else if (!MuxConsole.IsTui)
            // In TUI the count is folded into the session-header badge (see RenderTuiSessionHeader);
            // the standalone success line is suppressed there to avoid duplicate chrome.
            MuxConsole.WriteSuccess($"{singleAgentDef?.Name ?? "Agent"} has {filteredTools.Count} tools available");

        if (!MuxConsole.IsTui) MuxConsole.WriteLine();
        // Switch the (already-running) live-region driver into this session: in-session
        // palette scope + a fresh token meter. No-op outside TUI / when the footer is off.
        MuxConsole.EnableDockedFooter(topLevel: false);
        // Project the FULL session tool count for the header badge. The live toolset is assembled
        // further down (var singleAgentTools = [...]) and adds far more than the MCP tools in
        // filteredTools: 4 always-on local tools, the native REPL/shell tools, and the flag-gated
        // analyze_image / ask_user / delegation / team / giga tools. Mirror those exact conditions
        // here so the badge matches what the agent actually receives (was MCP-only -> undercount).
        bool hasVision = chatClientFactory != null && !string.IsNullOrEmpty(App.SwarmConfig?.VisionAgent?.Model);
        int projectedToolCount =
            4                                                   // listSkills, readSkill, sleep, mux_refresh
            + (hasVision ? 1 : 0)                               // analyze_image
            + filteredTools.Count                               // filtered MCP tools
            + ((shouldPlan || systemPromptOverride != null) ? 1 : 0) // ask_user
            + (allowSubAgents ? 1 : 0)                          // delegate_to_agent_lite
            + (allowParallelSubAgents ? 1 : 0)                  // delegate_parallel
            + (teamScope?.Tools.Count ?? 0)                     // team lead tools
            + ((App.GigaMode && teamScope is null) ? MuxSwarm.Utils.Teams.GigaMode.ToolCount : 0); // giga tools
        // G2: TUI session header card (no-op outside TUI render mode).
        MuxConsole.RenderTuiSessionHeader(
            singleAgentDef?.Name ?? "Agent",
            resolvedModelId,
            App.ActiveProvider?.Name ?? "",
            projectedToolCount);
        // The classic renderer separates the header from the transcript with a rule; the TUI
        // header card already provides that separation, so skip the extra rule there.
        if (!MuxConsole.IsTui) MuxConsole.WriteRule();

        string initialGoal;
        if (!string.IsNullOrWhiteSpace(incomingGoal))
        {
            initialGoal = incomingGoal;
            MuxConsole.WriteMuted($"[GOAL] {(goalId != null ? $"({goalId}) " : "")}{initialGoal}");
        }
        else
        {
            // First-turn parity: route the very first prompt through the same session-agnostic
            // meta dispatch the in-session loop uses, so /background, /daemon, /kanban, and the
            // slash-anywhere REPL hand-off work on turn one instead of being sent to the agent
            // as a goal. Loop until a real goal (or quit) arrives.
            initialGoal = "";
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!MuxConsole.TuiActive)
                    MuxConsole.WriteInline($"[{MuxConsole.PromptColor}]> [/]", "> ");
                string firstInput = MuxConsole.ReadInput(cancellationToken) ?? "";

                cancellationToken.ThrowIfCancellationRequested();

                if (IsQuitCommand(firstInput))
                {
                    MuxConsole.WriteSuccess("Exited from Chat interface successfully!");
                    return;
                }

                var firstDisp = await MetaCommandDispatch.TryHandleAsync(
                    firstInput, chatClientFactory, Models, cancellationToken);
                if (firstDisp == MetaCommandDispatch.Result.QuitToMenu) return;
                if (firstDisp == MetaCommandDispatch.Result.Handled) continue;

                initialGoal = firstInput;
                break;
            }
        }

        if (IsQuitCommand(initialGoal))
        {
            MuxConsole.WriteSuccess("Exited from Chat interface successfully!");
            return;
        }

        HookWorker.Enqueue(new HookEvent
        {
            Event = "user_input",
            Agent = singleAgentDef?.Name,
            Text = initialGoal,
            Timestamp = DateTimeOffset.UtcNow
        });

        OtelMetrics.RecordAgentMessage(singleAgentDef?.Name ?? string.Empty, "user", initialGoal);
        OtelMetrics.GoalsReceived.Add(1, new KeyValuePair<string, object?>("agent", singleAgentDef?.Name ?? string.Empty));

        if (string.IsNullOrEmpty(singleAgentDef?.SystemPromptPath))
        {
            MuxConsole.WriteError("[AGENT] singleAgent.promptPath not set in swarm.json.");
            return;
        }

        var systemPrompt = string.IsNullOrEmpty(systemPromptOverride) ? await Common.LoadPromptAsync(singleAgentDef.SystemPromptPath) : systemPromptOverride;

        var preamble = PreambleBuilder.Build(
            singleAgentDef.Name,
            continuous,
            shouldPlan,
            App.UltraMode);

        systemPrompt = preamble + "\r\n\r\n" + systemPrompt;

        var listSkillsTool = AIFunctionFactory.Create(
            method: () =>
            {
                var skills = SkillLoader.GetSkillMetadata(singleAgentDef?.Name);
                return string.Join("\r\n", skills.Select(s => $"- {s.Name}: {s.Description}"));
            },
            name: "list_skills",
            description: "List all available skills with their descriptions. Call this first to discover what skills are available before calling read_skill."
        );

        var readSkillTool = AIFunctionFactory.Create(
            method: (
                [System.ComponentModel.Description("Name of the skill to load. Call list_skills first if you are unsure of available skill names.")]
                string skillName
            ) =>
            {
                var content = SkillLoader.ReadSkill(skillName);
                if (content != null)
                    return content;

                var available = SkillLoader.GetSkillMetadata(singleAgentDef?.Name);
                var listing = string.Join("\r\n", available.Select(s => $"- {s.Name}: {s.Description}"));
                return $"Skill '{skillName}' not found. Here are the currently available skills — call read_skill again with a valid name:\r\n{listing}";
            },
            name: "read_skill",
            description: "Read the full instructions for a skill by name. Call list_skills first to discover available skills. " +
                         "Read the relevant skill BEFORE starting a task to follow its best practices."
        );

        var swarmVision = App.SwarmConfig?.VisionAgent;
        var visionModelId = swarmVision?.Model;

        var analyzeImageTool = chatClientFactory != null && !string.IsNullOrEmpty(visionModelId)
            ? LocalAiFunctions.CreateAnalyzeImageTool(chatClientFactory, visionModelId)
            : null;

        ChatOptions? compactChatOpts = MultiAgentOrchestrator.SwarmConfig?.CompactionAgent?.ModelOpts?.ToChatOptions();

        async Task<string> ExecuteDelegation(
            string agentName,
            string task,
            string callerName,
            bool restrictToSpecialists,
            bool parallel = false)
        {
            if (restrictToSpecialists && agentName == "Orchestrator")
                return "[ERROR] Sub-agents cannot delegate back to the Orchestrator.";

            if (!MultiAgentOrchestrator.Specialists.TryGetValue(agentName, out var specialist))
            {
                var available = restrictToSpecialists
                    ? string.Join(", ", MultiAgentOrchestrator.Specialists.Keys.Where(k => k != "Orchestrator"))
                    : string.Join(", ", MultiAgentOrchestrator.Specialists.Keys);
                return $"[ERROR] Unknown agent '{agentName}'. Available agents: {available}";
            }

            // The Orchestrator may not delegate to itself (it IS the coordinator). A specialist
            // agent, however, is just a persona (prompt + memory + tools) - nothing caps it to a
            // single concurrent instance, so it MAY fan out to its own kind (each self-delegation
            // spawns an isolated cleanSession sub-agent, depth-bounded by maxSubAgentIterations).
            if (agentName == callerName && callerName.Equals("Orchestrator", System.StringComparison.OrdinalIgnoreCase))
                return $"[ERROR] The Orchestrator cannot delegate to itself.";


            using var delegationSpan = OtelTracer.GetSource().StartActivity("delegation");
            delegationSpan?.SetTag("from", callerName);
            delegationSpan?.SetTag("to", agentName);
            var delegationSw = Stopwatch.StartNew();

            MuxConsole.WriteDelegation(callerName, agentName, task);


            int attempts = 0;
            // Children must observe the PER-TURN cancel token too: Esc cancels the lead's turnCts
            // (registered on StdinCancelMonitor), not this captured app/session token. Linking them
            // means pressing Esc tears the sub-agent down instead of wedging the lead on its await.
            using var delLinked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, StdinCancelMonitor.Instance?.ActiveTurnToken ?? CancellationToken.None);
            var (rawResult, status, summary, artifacts) = await MultiAgentOrchestrator.RunSubAgentAsync(
                specialist, task, ExecutionLimits.Current.MaxSubTaskRetries, delLinked.Token, prodMode: false, cleanSession: true);

            bool succeeded = status == "success";
            attempts += 1;

            if (!succeeded)
            {
                if (attempts >= ExecutionLimits.Current.MaxSubTaskRetries)
                    MuxConsole.WriteError($"{agentName} failed {ExecutionLimits.Current.MaxSubTaskRetries} times on this sub-task.");
            }

            string compacted = "";
            MuxSwarm.Utils.DelegationStore.Retained? retained = null;
            await MuxConsole.WithSpinnerAsync($"Compacting {agentName} result", async () =>
            {
                // Resolve a compaction client for sub-agent result summarization. The swarm-only
                // static MultiAgentOrchestrator.CompactionClient is NULL in a single-agent session
                // (MultiAgentOrchestrator.RunAsync never ran), which previously forced extractive-
                // only compaction here regardless of subAgentSummaryMode. Prefer the lazily-resolved
                // compaction-agent client (same one /compact uses), then the swarm static, and fall
                // back to the active session model so auto/llm modes actually summarize.
                var subCompactionClient = ResolveCompactionClient()
                    ?? MultiAgentOrchestrator.CompactionClient
                    ?? client;
                (compacted, retained) = await DelegationStore.TierResultAsync(
                    DelegationStore.CurrentScope,
                    agentName,
                    rawResult,
                    status,
                    summary,
                    artifacts,
                    subCompactionClient,
                    compactChatOpts);
            });

            delegationResults.Add(new MultiAgentOrchestrator.DelegationResult(agentName, compacted, status, summary, artifacts,
                retained?.Handle, retained?.Path, retained?.RawLen ?? 0));

            if (!succeeded && attempts >= ExecutionLimits.Current.MaxSubTaskRetries)
            {
                compacted += $"\r\n[RETRY_EXHAUSTED] {agentName} failed {ExecutionLimits.Current.MaxSubTaskRetries} attempts. " +
                             "Consider a different approach or agent, or surface this to the user.";
            }

            delegationSw.Stop();
            OtelMetrics.Delegations.Add(1,
                new KeyValuePair<string, object?>("from", callerName),
                new KeyValuePair<string, object?>("to", agentName));
            OtelMetrics.DelegationDuration.Record(delegationSw.ElapsedMilliseconds,
                new KeyValuePair<string, object?>("to", agentName));

            if (!succeeded)
                OtelMetrics.SubTaskRetries.Add(1, new KeyValuePair<string, object?>("agent", agentName));

            return string.IsNullOrWhiteSpace(compacted)
                ? $"[{agentName} completed but returned no output]"
                : compacted;
        }

        IChatClient? compactionClient = null;
        ChatOptions? compactionChatOptions = null;
        bool compactionResolved = false;

        IChatClient? ResolveCompactionClient()
        {
            if (compactionResolved) return compactionClient;
            compactionResolved = true;

            if (chatClientFactory == null) return null;

            try
            {
                var swarmJson = File.ReadAllText(MultiAgentOrchestrator.SwarmConfPath);
                var swarmConfig = JsonSerializer.Deserialize<SwarmConfig>(swarmJson);

                if (swarmConfig?.CompactionAgent != null)
                {
                    if (!string.IsNullOrEmpty(swarmConfig.CompactionAgent.Model))
                        compactionClient = chatClientFactory(swarmConfig.CompactionAgent.Model);

                    if (swarmConfig.CompactionAgent.AutoCompactTokenThreshold > 0)
                        autoCompactTokenThreshold = swarmConfig.CompactionAgent.AutoCompactTokenThreshold;

                    compactionChatOptions = swarmConfig.CompactionAgent.ModelOpts?.ToChatOptions();
                }

                var compactionModel = swarmConfig?.CompactionAgent?.Model;

                if (!string.IsNullOrEmpty(compactionModel))
                    compactionClient = chatClientFactory(compactionModel);
            }
            catch { /* no compaction available */ }

            return compactionClient;
        }

        var subAgentDelegateTool = AIFunctionFactory.Create(
            method: async (
                [Description("Name of the specialist agent to delegate to. Cannot delegate to Orchestrator.")] string agentName,
                [Description("The specific sub-task or instruction for the specialist agent")] string task
            ) =>
            {
                var specialists = MultiAgentOrchestrator.Specialists;
                if (agentName == "Orchestrator")
                    return "[ERROR] Sub-agents cannot delegate back to the Orchestrator.";

                if (!specialists.TryGetValue(agentName, out var specialist))
                {
                    var available = string.Join(", ", specialists.Keys.Where(k => k != "Orchestrator"));
                    return $"[ERROR] Unknown agent '{agentName}'. Available agents: {available}";
                }

                return await ExecuteDelegation(agentName, task, singleAgentDef.Name, restrictToSpecialists: true);
            },
            name: "delegate_to_agent_lite",
            description: "Delegate a sub-task to an agent by name. " +
                         "Use when a task would be better handled by another agent based on their specialization, or when offloading would improve efficiency. " +
                         "Cannot delegate to the Orchestrator. Note: Synchronous Version. " +
                         Common.DelegableAgentNames()
        );

        var delegateParallelTool = AIFunctionFactory.Create(
            method: async (
                [Description("A list of agent assignments to run simultaneously")]
                IEnumerable<ParallelSwarmOrchestrator.ParallelTaskRequest> assignments,
                [Description("When true, fire the tasks into the BACKGROUND and return their job ids " +
                    "IMMEDIATELY (non-blocking) so you keep working; poll/collect later with check_delegations. " +
                    "Default false blocks until the whole batch finishes and returns all results.")]
                bool background = false
            ) =>
            {
                if (chatClientFactory == null)
                    return "[Error] Cannot create chat client for agent, chat client is null";

                var specialists = MultiAgentOrchestrator.Specialists;
                var assignmentList = assignments?.ToList() ?? new();
                if (assignmentList.Count == 0)
                    return "[delegate_parallel] No assignments given. Provide a list of {AgentName, Task}.";

                // Non-blocking path: fire each task into the background via DetachedRunner and return
                // job ids at once so the lead keeps working. Collect with check_delegations.
                if (background)
                {
                    var launched = new List<string>();
                    var failed = new List<string>();
                    foreach (var req in assignmentList)
                    {
                        var job = await DetachedRunner.LaunchAsync(
                            req.AgentName, req.Task, chatClientFactory, Models,
                            StdinCancelMonitor.Instance?.ActiveTurnToken ?? cancellationToken);
                        if (job is null) failed.Add(req.AgentName);
                        else launched.Add($"{job.Id} <- {req.AgentName}");
                    }

                    var bg = new StringBuilder();
                    bg.AppendLine($"[delegate_parallel · background] Launched {launched.Count} background job(s); they run while you continue.");
                    foreach (var l in launched) bg.AppendLine($"  {l}");
                    if (failed.Count > 0)
                        bg.AppendLine($"  Could not launch: {string.Join(", ", failed)} (unknown agent?)");
                    bg.AppendLine("Keep working. Call check_delegations (optionally with these ids) to poll status and collect results.");
                    return bg.ToString();
                }

                MuxConsole.WriteInfo($"[CLASSROOM] Dispatching {assignmentList.Count} tasks concurrently...");

                // Link the captured app/session token with the live PER-TURN token so Esc (which
                // cancels turnCts) unwinds the whole parallel batch. Otherwise the lead blocks on
                // Task.WhenAll while the children run on an un-cancelled token -> input deadlock
                // that only a restart clears.
                using var batchLinked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, StdinCancelMonitor.Instance?.ActiveTurnToken ?? CancellationToken.None);
                var batchCt = batchLinked.Token;

                var taskBatch = assignmentList.Select(async req =>
                {
                    await semaphore.WaitAsync(batchCt);
                    try
                    {
                        // Resolve a compaction client for the parallel workers. The local
                        // compactionClient is only populated lazily by ResolveCompactionClient()
                        // (via /compact); a parallel delegation before any /compact would otherwise
                        // pass null -> extractive-only regardless of subAgentSummaryMode. Resolve +
                        // fall back to the active session model so auto/llm modes summarize.
                        var batchCompactionClient = ResolveCompactionClient()
                            ?? MultiAgentOrchestrator.CompactionClient
                            ?? client;
                        return await ParallelSwarmOrchestrator.ExecuteParallelWorker(
                            req.AgentName, req.Task, singleAgentDef.Name,
                            specialists, pDelegationResults, retryRegistry,
                            chatClientFactory, Models, batchCompactionClient, compactionChatOptions,
                            maxIterations, false, ct: batchCt, cleanSession: true);
                    }
                    catch (OperationCanceledException) when (batchCt.IsCancellationRequested)
                    {
                        // A real turn cancellation (Esc / kill-switch) must propagate so Task.WhenAll
                        // unwinds the whole batch - not be swallowed as a per-agent result.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // A single agent throwing (rate-limit, provider/network error, etc.) must NOT
                        // discard every sibling result. Turn the throw into this agent's error string
                        // so Task.WhenAll still returns and the batch keeps all other agents' output.
                        return $"[ERROR {req.AgentName}] {ex.Message}";
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                var results = await Task.WhenAll(taskBatch);

                var synthesis = new StringBuilder();
                synthesis.AppendLine("### PARALLEL BATCH COMPLETED ###");
                foreach (var res in results) synthesis.AppendLine(res);
                return synthesis.ToString();
            },
            name: "delegate_parallel",
            description: "Executes multiple sub-tasks simultaneously. Use this for independent tasks like " +
                         "researching different topics or auditing multiple files. Each assignment specifies " +
                         "an AgentName and a Task string. Blocks until all finish and returns their results. " +
                         "Pass background=true to instead launch them in the background and return job ids at once " +
                         "(poll with check_delegations) when you have other work to do meanwhile. " +
                         Common.DelegableAgentNames()
        );

        // Poll + collect background delegations launched via delegate_parallel(background:true) (and /background jobs).
        var checkDelegationsTool = AIFunctionFactory.Create(
            method: (
                [Description("Optional job id (e.g. bg3) to check just one; omit to list ALL background jobs.")]
                string? jobId
            ) =>
            {
                var jobs = DetachedRunner.Jobs();
                if (!string.IsNullOrWhiteSpace(jobId))
                    jobs = jobs.Where(j => j.Id.Equals(jobId.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

                if (jobs.Count == 0)
                    return string.IsNullOrWhiteSpace(jobId)
                        ? "[check_delegations] No background jobs. Launch some with delegate_parallel(background:true)."
                        : $"[check_delegations] No background job with id '{jobId}'.";

                var sb = new System.Text.StringBuilder();
                int running = jobs.Count(j => j.Status == DetachedStatus.Running);
                sb.AppendLine($"[check_delegations] {jobs.Count} job(s), {running} still running.");
                foreach (var j in jobs)
                {
                    sb.AppendLine($"- {j.Id} [{j.Agent}] {j.Status}");
                    if (j.Status != DetachedStatus.Running && !string.IsNullOrWhiteSpace(j.Result))
                    {
                        var r = j.Result!.Length > 4000 ? j.Result[..4000] + "\n... (truncated)" : j.Result;
                        sb.AppendLine($"  result:\n{r}");
                    }
                }
                if (running > 0) sb.AppendLine("Some jobs are still running; call check_delegations again later to collect them.");
                return sb.ToString();
            },
            name: "check_delegations",
            description: "Poll the status of background delegations launched via delegate_parallel(background:true) (and /background jobs). " +
                         "Pass a job id to check one, or omit to list all. Returns each job's status and, for finished jobs, " +
                         "its full result so you can collect work you fired earlier without blocking."
        );

        var singleAgentTools = (IList<AITool>)
        [
            listSkillsTool, readSkillTool, LocalAiFunctions.SleepTool, LocalAiFunctions.MuxRefreshTool,
            .. (analyzeImageTool != null ? new[] { analyzeImageTool } : Array.Empty<AITool>()),
            .. filteredTools
        ];

        if (shouldPlan || systemPromptOverride != null) singleAgentTools.Add(LocalAiFunctions.AskUserTool);

        if (allowSubAgents)
        {
            singleAgentTools.Add(subAgentDelegateTool);
            await MultiAgentOrchestrator.BuildSpecialists(Models, modelId => App.CreateChatClient(modelId),
                (App.McpTools ?? throw new InvalidOperationException()).Cast<AITool>().ToList());
        }

        if (allowParallelSubAgents)
        {
            singleAgentTools.Add(delegateParallelTool);
            await MultiAgentOrchestrator.BuildSpecialists(Models, modelId => App.CreateChatClient(modelId),
                (App.McpTools ?? throw new InvalidOperationException()).Cast<AITool>().ToList());
        }

        // Size-tiered context passing: whenever this lead can spawn sub-agents (sequential, parallel,
        // or a team), grant read_delegation so it can surgically pull the FULL raw output of any
        // delegation whose result was spilled to disk and returned as a pointer/handle. Covers
        // /sub, /psub, /ultra, /giga, and team-lead; swarm/pswarm get it via the orchestrator lists.
        if (allowSubAgents || allowParallelSubAgents || teamScope is not null || App.GigaMode)
            singleAgentTools.Add(LocalAiFunctions.ReadDelegationTool);

        // Non-blocking delegation: when this lead can spawn sub-agents, also grant check_delegations
        // (delegate_parallel(background:true) fires work into the background + returns at once;
        // check_delegations polls/collects). Optional alongside the blocking tools. Same gate as
        // read_delegation.
        if (allowSubAgents || allowParallelSubAgents || teamScope is not null || App.GigaMode)
        {
            singleAgentTools.Add(checkDelegationsTool);
        }

        // v0.12.0 M2 - team lead: append the team tools (team_dispatch + taskboard tools) and
        // build the member specialist registry so member dispatch (ExecuteParallelWorker) can
        // resolve them. Members surface live in the Agent View via the existing capture path.
        // Null teamScope leaves the tool list and registry exactly as the off-team path built them.
        if (teamScope is not null)
        {
            // M4: pass the team's per-member extra-tool factory so each MEMBER specialist is built
            // with its own identity-bound send_message/read_inbox (null = no team mailbox).
            await MultiAgentOrchestrator.BuildSpecialists(Models, modelId => App.CreateChatClient(modelId),
                (App.McpTools ?? throw new InvalidOperationException()).Cast<AITool>().ToList(),
                teamScope.MemberToolFactory);
            foreach (var t in teamScope.Tools) singleAgentTools.Add(t);
            // Append the concise teams-coordination guide ONLY while leading a team. Off-team this
            // block is skipped, so the single-agent system prompt is byte-identical to before.
            systemPrompt += teamScope.LeadPreamble();
        }

        // v0.12.0 M6 Giga mode: grant the live single agent dynamic-orchestration tools (spawn_team,
        // run_team, write/run/list workflows) + a capability-reference preamble. Members run through
        // the shared ExecuteParallelWorker path, so specialists must be built. Off-giga (the default)
        // this block is skipped entirely -> byte-identical single-agent prompt + toolset.
        if (App.GigaMode && teamScope is null)
        {
            await MultiAgentOrchestrator.BuildSpecialists(Models, modelId => App.CreateChatClient(modelId),
                (App.McpTools ?? throw new InvalidOperationException()).Cast<AITool>().ToList());
            foreach (var t in MuxSwarm.Utils.Teams.GigaMode.BuildTools(
                modelId => App.CreateChatClient(modelId), Models, cancellationToken))
                singleAgentTools.Add(t);
            systemPrompt += MuxSwarm.Utils.Teams.GigaMode.Preamble();
        }

        var agentChatOptions = new ChatOptions
        {
            Instructions = systemPrompt,
            Tools = [.. singleAgentTools!]
        };

        // Re-seed the live "/tools" palette with the FULLY-assembled toolset (the early seed only
        // had the MCP tools). Now the expandable badge view lists exactly what the agent holds.
        MuxConsole.SetTuiToolsCatalog(singleAgentTools.Select(t => (t.Name, t.Description ?? "")).ToList());

        // G11: static context-overhead breakdown for the footer. On a FRESH session the context
        // is dominated by the system prompt + the serialized tool/MCP schemas (names, descriptions,
        // JSON parameter shapes), NOT the conversation - which is why a brand-new session can
        // already read tens of thousands of tokens. Estimate each once (~2.5 chars/token, matching
        // Common.EstimateTokenCount) and push to the docked footer so the user understands the baseline.
        {
            int sysChars = systemPrompt?.Length ?? 0;
            int toolChars = 0;
            foreach (var t in singleAgentTools!)
            {
                toolChars += t.Name?.Length ?? 0;
                toolChars += t.Description?.Length ?? 0;
                if (t is Microsoft.Extensions.AI.AIFunction fn)
                {
                    try { toolChars += fn.JsonSchema.GetRawText().Length; }
                    catch { /* schema not serializable - skip */ }
                }
            }
            uint sysTok = (uint)Math.Ceiling(sysChars / 2.5);
            uint toolTok = (uint)Math.Ceiling(toolChars / 2.5);
            MuxConsole.SetTuiTokenBreakdown(sysTok, toolTok);
            CostLedger.SetStatic(resolvedModelId, sysTok, toolTok);
        }

        // Merge modelOpts from swarm.json if present
        var singleAgentOpts = GetSingleAgentModelOpts();
        if (singleAgentOpts is not null)
        {
            var modelChatOpts = singleAgentOpts.ToChatOptions();
            if (modelChatOpts is not null)
            {
                agentChatOptions.Temperature = modelChatOpts.Temperature;
                agentChatOptions.TopP = modelChatOpts.TopP;
                agentChatOptions.TopK = modelChatOpts.TopK;
                agentChatOptions.MaxOutputTokens = modelChatOpts.MaxOutputTokens;
                agentChatOptions.FrequencyPenalty = modelChatOpts.FrequencyPenalty;
                agentChatOptions.PresencePenalty = modelChatOpts.PresencePenalty;
                agentChatOptions.Seed = modelChatOpts.Seed;
                agentChatOptions.Reasoning = modelChatOpts.Reasoning;
                agentChatOptions.AdditionalProperties = modelChatOpts.AdditionalProperties;
            }
        }

        // NOTE: config.json showReasoning is a CLIENT-SIDE display gate applied in
        // MuxConsole.WriteStream (drops streamed reasoning text when "none"); it intentionally
        // does NOT touch the native API reasoning level here. A per-agent swarm.json
        // modelOpts.reasoning.output still controls the API level where set.

        // /ultra: escalate reasoning to the provider max (numeric budget + effort tier).
        // Applied last so it wins over swarm.json modelOpts for this session only.
        if (App.UltraMode)
            UltraReasoning.Apply(agentChatOptions);

        AIAgent? agent = client?.AsAIAgent(new ChatClientAgentOptions
        {
            Name = singleAgentDef.Name,
            ChatOptions = agentChatOptions
        });

        if (agent == null)
        {
            MuxConsole.WriteError($"[AGENT] Failed to initialize {singleAgentDef.Name}. Verify your configuration and API credentials.");
            return;
        }

        string currentGoal = initialGoal;

        var sessionTimestamp = resumedSessionDir != null
            ? Path.GetFileName(resumedSessionDir)
            : DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

        // Surface the active session id in the docked footer; the badge replaces the noisy
        // per-save "[AGENT SESSION] Saved to ..." confirmation (now suppressed under TUI).
        MuxConsole.SetTuiSessionId(sessionTimestamp);

        // Size-tiered delegation retention scope: a single-agent session that spawns sub-agents
        // (/sub, /psub, /ultra, /giga, team-lead) keys its spilled raw + cumulative lead-cap counter
        // off this session id. MAO.RunAsync never runs here, so set it on the single-agent path.
        DelegationStore.SetScope(sessionTimestamp);
        DelegationStore.ResetScope(sessionTimestamp);

        var session = resumedSession.HasValue
            ? await agent.DeserializeSessionAsync(resumedSession.Value)
            : await agent.CreateSessionAsync();

        var conversationHistory = resumedSession.HasValue
            ? Common.ExtractMessagesFromSession(resumedSession.Value)
            : new List<ChatMessage>();

        if (resumedSession.HasValue)
            MuxConsole.WriteSuccess($" Extracted {conversationHistory.Count} messages from resumed session");

        // Deep memory (reflectionAgent.mode == "deep"): expose this session's history to the
        // background gatherer and start its activity-gated loop. Inert in standard mode; interactive
        // only (the gatherer makes its own LLM calls, never in serve/acp/stdio - gated by caller).
        if (!MuxConsole.StdioMode && App.ServePort <= 0)
        {
            // Fresh lead session: clear the per-session injected-id tracking so the first turn's
            // full block + later deltas are computed cleanly for this session.
            MuxSwarm.Utils.Memory.ReflectionInjector.ResetSession();
            MuxSwarm.Utils.Memory.ReflectionGatherer.HistoryProvider =
                () => conversationHistory.ToList();
            MuxSwarm.Utils.Memory.ReflectionGatherer.Start(chatClientFactory, cancellationToken);
        }

        _pendingCompaction = false;
        async Task<bool> TryCompactAsync(string? instruction = null)
        {
            using var compactSpan = OtelTracer.GetSource().StartActivity("compaction");
            compactSpan?.SetTag("agent", singleAgentDef?.Name);
            var compactSw = Stopwatch.StartNew();

            var cc = ResolveCompactionClient();
            if (cc == null)
            {
                MuxConsole.WriteWarning("No compaction model configured. Set compactionAgent in swarm.json.");
                return false;
            }

            uint beforeTokens = _sessionTokens > 0
                ? _sessionTokens
                : (uint)Common.EstimateTokenCount(conversationHistory);
            ChatMessage? compactedMsg = null;

            ServeMode.EmitEvent(new { type = "compaction_start" });

            await MuxConsole.WithSpinnerAsync("Compacting conversation history", async () =>
            {
                compactedMsg = await ResultCompactor.CompactConversationAsync(
                    conversationHistory, cc, chatOptions: compactionChatOptions, instruction: instruction);

                session = await agent.CreateSessionAsync();
                conversationHistory.Clear();

                conversationHistory.Add(new ChatMessage(ChatRole.User, compactedMsg.Text));

                conversationHistory.Add(new ChatMessage(ChatRole.Assistant,
                    "Context restored. Ready to continue."));
            });

            compactSw.Stop();
            OtelMetrics.CompactionRuns.Add(1);
            CostLedger.RecordCompaction(resolvedModelId);
            OtelMetrics.CompactionDuration.Record(compactSw.ElapsedMilliseconds);
            if (beforeTokens > 0)
                OtelMetrics.CompactionRatio.Record((double)_sessionTokens / beforeTokens);

            _pendingCompaction = true;
            _sessionTokens = (uint)Common.EstimateTokenCount(conversationHistory);
            MuxConsole.WriteSuccess($"Compacted: {beforeTokens:N0} -> {_sessionTokens:N0} tokens");
            ServeMode.EmitEvent(new { type = "compaction_done", fromTokens = beforeTokens, toTokens = _sessionTokens });
            return _pendingCompaction;
        }

        //Offer option to compact
        if (resumedSession.HasValue)
        {
            int estimatedResumeTokens = Common.EstimateTokenCount(resumedSession.Value);
            if (estimatedResumeTokens > autoCompactTokenThreshold)
            {
                MuxConsole.WriteWarning($"Resumed session is large (~{estimatedResumeTokens:N0} tokens, threshold: {autoCompactTokenThreshold:N0}).");
                _sessionTokens = (uint)estimatedResumeTokens;

                if (MuxConsole.Confirm("Compact before continuing?"))
                    await TryCompactAsync();
            }
        }

        AutoCompactThreshold = autoCompactTokenThreshold ?? 80_000;

        // Batch6: /undo and /retry rebuild the provider session from a trimmed local
        // history (the SDK session is opaque/append-only), mirroring TryCompactAsync.
        async Task RebuildSessionFromHistoryAsync()
        {
            session = await agent.CreateSessionAsync();
            _sessionTokens = (uint)Common.EstimateTokenCount(conversationHistory);
            _pendingReseed = conversationHistory.Count > 0;
        }

        // Batch6: /undo -- drop the last user+assistant exchange, rebuild the session.
        async Task<bool> TryUndoAsync()
        {
            int lastAssistant = -1;
            for (int i = conversationHistory.Count - 1; i >= 0; i--)
                if (conversationHistory[i].Role == ChatRole.Assistant) { lastAssistant = i; break; }
            if (lastAssistant < 0)
            {
                MuxConsole.WriteWarning("Nothing to undo.");
                return false;
            }
            int prevUser = -1;
            for (int i = lastAssistant - 1; i >= 0; i--)
                if (conversationHistory[i].Role == ChatRole.User) { prevUser = i; break; }
            int removeFrom = prevUser >= 0 ? prevUser : lastAssistant;
            int removed = conversationHistory.Count - removeFrom;
            conversationHistory.RemoveRange(removeFrom, removed);
            await RebuildSessionFromHistoryAsync();
            MuxConsole.WriteSuccess($"Undid last exchange ({removed} message(s) dropped). ~{_sessionTokens:N0} tokens remain.");
            return true;
        }

        // Batch6: /retry -- trim the last assistant (and its user) so the user
        // message can be re-run. Returns the user text to re-submit, or null.
        string? TryPrepareRetry()
        {
            int lastAssistant = -1;
            for (int i = conversationHistory.Count - 1; i >= 0; i--)
                if (conversationHistory[i].Role == ChatRole.Assistant) { lastAssistant = i; break; }
            int searchFrom = lastAssistant >= 0 ? lastAssistant - 1 : conversationHistory.Count - 1;
            int lastUser = -1;
            for (int i = searchFrom; i >= 0; i--)
                if (conversationHistory[i].Role == ChatRole.User) { lastUser = i; break; }
            if (lastUser < 0)
            {
                MuxConsole.WriteWarning("Nothing to retry.");
                return null;
            }
            string userText = conversationHistory[lastUser].Text ?? string.Empty;
            conversationHistory.RemoveRange(lastUser, conversationHistory.Count - lastUser);
            return userText;
        }

        // Batch6: /tokens -- context usage meter.
        void PrintTokenUsage()
        {
            int threshold = autoCompactTokenThreshold ?? 80_000;
            double pct = threshold > 0 ? (double)_sessionTokens / threshold * 100.0 : 0;
            MuxConsole.WriteInfo(
                $"Context: {_sessionTokens:N0} / {threshold:N0} tokens ({pct:F1}%) - "
                + $"{conversationHistory.Count} message(s) - {_cachedTokens:N0} cached.");
        }

        // G11 / v0.12: live token estimate during a streaming turn. The provider reports the
        // AUTHORITATIVE token count only via UsageContent frames, so between those frames the
        // docked meter would otherwise sit frozen. We tick a throttled estimate that extrapolates
        // FORWARD from the last ground-truth checkpoint: est = _sessionTokens (last authoritative
        // total) + (output chars streamed SINCE that checkpoint / ~2.5).
        //
        // Reconciliation model (matches Codex/Cline): every UsageContent frame snaps _sessionTokens
        // /_cachedTokens to truth AND resets _liveTurnChars to 0 (see ReconcileLiveBaseline), so the
        // estimate only ever bridges the small gap since the last checkpoint -- never the whole turn.
        // This eliminates the old "drops on turn end" artifact, which came from (a) counting tool-call
        // arg + tool-result CHARS as live tokens (they are already folded into the provider's
        // InputTokenCount on the next iteration -- double-counting) and (b) a cached-offset that
        // shifted mid-turn. We now tick ONLY on streamed answer + reasoning text (output the model is
        // actively generating that is not yet in any usage frame). The meter still inherits the REAL
        // post-cache drop from the provider numbers -- that is honest, not an estimate error, so we do
        // NOT clamp it monotonically (no mature harness does; it would hide the true caching effect).
        long _liveTurnChars = 0;
        DateTime _lastLivePush = DateTime.MinValue;
        void ResetLiveTokenEstimate() { _liveTurnChars = 0; _lastLivePush = DateTime.MinValue; }
        // Snap the live-estimate baseline to authoritative usage: zero the per-checkpoint char
        // accumulator so the next estimate extrapolates forward from the just-arrived ground truth.
        void ReconcileLiveBaseline() { _liveTurnChars = 0; _lastLivePush = DateTime.MinValue; }
        void TickLiveTokens(int addedChars)
        {
            if (!MuxConsole.TuiActive) return;
            _liveTurnChars += Math.Max(0, addedChars);
            // Throttle to ~20 Hz: responsive live motion without repainting the region on every
            // micro-chunk.
            var now = DateTime.UtcNow;
            if ((now - _lastLivePush).TotalMilliseconds < 50) return;
            _lastLivePush = now;
            uint threshold = (uint)(autoCompactTokenThreshold ?? 80_000);
            uint displayEst = LiveTokenMeter.Estimate(_sessionTokens, _liveTurnChars, _cachedTokens);
            MuxConsole.RenderTuiStatusBar(displayEst, threshold,
                App.PlanMode, App.UltraMode, App.ParallelSubAgentsMode, _cachedTokens, App.SubAgentsMode, App.GigaMode);
        }

        // G7: live context-meter + mode-badge status bar (TUI render mode only; no-op otherwise).
        void RenderStatusBar()
        {
            uint threshold = (uint)(autoCompactTokenThreshold ?? 80_000);
            // Show the displayed context as tokens minus cached (Claude-style): the system
            // prompt is cached after the first turn, so excluding cached gives the user a
            // truer picture of "live" context growth rather than a misleading static base.
            uint displayTokens = _sessionTokens > _cachedTokens ? _sessionTokens - _cachedTokens : _sessionTokens;
            MuxConsole.RenderTuiStatusBar(displayTokens, threshold,
                App.PlanMode, App.UltraMode, App.ParallelSubAgentsMode, _cachedTokens, App.SubAgentsMode, App.GigaMode);
        }

        // /effort + Shift+Tab: cycle the live reasoning-effort tier for this session. Mutates
        // the already-built agentChatOptions.Reasoning in place so the next turn uses it,
        // exactly like /ultra's escalation but user-cyclable. Order: low -> med -> high -> xhigh -> low.
        // The same underlying state backs the typed /effort command and the Shift+Tab key, so
        // they stay consistent. The footer chip reflects the current tier. "xhigh" (ExtraHigh) is the
        // top tier the reasoning API exposes; ReasoningEffortFallbackClient degrades it to high per-model
        // if the endpoint rejects the wire value, so cycling to it never fails a turn.
        string[] effortTiers = { "low", "med", "high", "xhigh" };
        int effortIdx = -1;   // -1 = unset (inherit config/model default), no chip shown
        string ApplyEffortTier(string tier)
        {
            var eff = tier switch
            {
                "low"  => Microsoft.Extensions.AI.ReasoningEffort.Low,
                "med"  => Microsoft.Extensions.AI.ReasoningEffort.Medium,
                "high" => Microsoft.Extensions.AI.ReasoningEffort.High,
                "xhigh" => Microsoft.Extensions.AI.ReasoningEffort.ExtraHigh,
                _      => Microsoft.Extensions.AI.ReasoningEffort.Medium
            };
            agentChatOptions.Reasoning = new Microsoft.Extensions.AI.ReasoningOptions
            {
                Effort = eff,
                Output = agentChatOptions.Reasoning?.Output
            };
            return tier;
        }
        string CycleEffort()
        {
            effortIdx = (effortIdx + 1) % effortTiers.Length;
            var tier = effortTiers[effortIdx];
            ApplyEffortTier(tier);
            return tier;
        }
        void SetEffortByName(string name)
        {
            var n = name.Trim().ToLowerInvariant();
            n = n switch { "medium" => "med", "m" => "med", "l" => "low", "h" => "high", "extrahigh" => "xhigh", "extra_high" => "xhigh", "xh" => "xhigh", "max" => "xhigh", _ => n };
            int idx = Array.IndexOf(effortTiers, n);
            if (idx < 0)
            {
                MuxConsole.WriteWarning($"Unknown effort '{name}'. Use low, med, high, or xhigh.");
                return;
            }
            effortIdx = idx;
            ApplyEffortTier(n);
            MuxConsole.SetTuiEffort(n);
            MuxConsole.WriteSuccess($"Reasoning effort set to {n}.");
        }

        // Seed the footer effort chip from the resolved reasoning config so it renders by
        // default at session init (rather than only after a manual /effort). Maps the
        // provider effort tier back to our low/med/high label; leaves it hidden if unset.
        {
            var seededEffort = agentChatOptions.Reasoning?.Effort;
            string? seedTier =
                seededEffort == Microsoft.Extensions.AI.ReasoningEffort.Low    ? "low"  :
                seededEffort == Microsoft.Extensions.AI.ReasoningEffort.Medium ? "med"  :
                seededEffort == Microsoft.Extensions.AI.ReasoningEffort.High   ? "high" :
                seededEffort == Microsoft.Extensions.AI.ReasoningEffort.ExtraHigh ? "xhigh" : null;
            if (seedTier is not null)
            {
                effortIdx = Array.IndexOf(effortTiers, seedTier);
                MuxConsole.SetTuiEffort(seedTier);
            }
        }
        // Register Shift+Tab to cycle effort and report the new chip label.
        MuxConsole.SetTuiModeCycle(() => CycleEffort());

        var lastPersistTime = DateTime.UtcNow;
        do
        {

            cancellationToken.ThrowIfCancellationRequested();

            if (_sessionTokens > autoCompactTokenThreshold)
            {
                MuxConsole.WriteInfo($"Context approaching limit (~{_sessionTokens:N0} tokens). Auto-compacting...");
                await TryCompactAsync();
            }

            // The interrupted exchange (if any) is now replayed from the session's own
            // in-memory history (synced in the cancellation catch below), so the turn
            // payload is always just the new goal.
            List<ChatMessage> messages = [new(ChatRole.User, currentGoal)];

            conversationHistory.Add(new ChatMessage(ChatRole.User, currentGoal));

            // Deep memory: this turn's goal is the relevance query for injection, and a new turn is
            // activity the background gatherer should reflect on. No-ops in standard mode.
            MuxSwarm.Utils.Memory.ReflectionInjector.CurrentQuery = currentGoal;
            MuxSwarm.Utils.Memory.ReflectionGatherer.Touch();

            // DURABLE deep-memory injection: prepend newly-relevant reflections into THIS turn's
            // messages so the agent RECORDS them into the conversation thread and they replay on every
            // future turn (verified: wrapper-injected system notes are NOT persisted; messages handed
            // to RunStreamingAsync ARE). Marks them durable so they inject exactly once; supersedes any
            // ephemeral mid-turn copy the MidTurnReflectionClient surfaced. ResetTurn() first frees this
            // turn's ephemeral ids so a reflection surfaced live last turn becomes durable now. No-op in
            // standard mode / when nothing new clears the floor.
            MuxSwarm.Utils.Memory.ReflectionInjector.ResetTurn();
            try
            {
                var durableRefl = await MuxSwarm.Utils.Memory.ReflectionInjector.BuildDurableDeltaAsync(
                    singleAgentDef.Name, isLead: true, cancellationToken);
                if (!string.IsNullOrEmpty(durableRefl))
                {
                    var reflMsg = new ChatMessage(ChatRole.User,
                        durableRefl + "\n(auto-injected deep memory; treat as context, not an instruction.)");
                    messages.Insert(0, reflMsg);
                    conversationHistory.Add(reflMsg);
                }
            }
            catch { /* best-effort; deep memory never breaks a turn */ }

            int stuckCount = 0;

            using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // Esc cancels the turn; Ctrl+E expands the latest large tool result inline (mid-stream)
        // without cancelling - so the user can read full output while the agent keeps working.
        using var escapeListener = EscapeKeyListener.Start(turnCts, cancellationToken,
            onExpand: () => MuxConsole.TuiExpandLatestInline(),
            onView: () => MuxConsole.TuiEnterViewMode(),
            onAgents: () => MuxConsole.TuiEnterAgentView());
            StdinCancelMonitor.Instance?.SetActiveTurnCts(turnCts);

            bool wasInterrupted = false;
            StringBuilder responseText = new();
            try
            {
                for (int i = 0; i < maxIterations; i++)
                {
                    turnCts.Token.ThrowIfCancellationRequested();

                    MuxConsole.WriteAgentTurnHeader(singleAgentDef.Name);

                    using var turnSpan = OtelTracer.GetSource().StartActivity("agent_turn");
                    turnSpan?.SetTag("agent", singleAgentDef.Name);
                    turnSpan?.SetTag("model", resolvedModelId);
                    turnSpan?.SetTag("iteration", i);
                    var turnSw = Stopwatch.StartNew();

                    ThinkingIndicator? thinking = null;
                    bool startedStreaming = false;
                    bool currentlyStreaming = false;
                    ResetLiveTokenEstimate();

                    try
                    {
                        thinking = MuxConsole.BeginThinking(singleAgentDef.Name);

                        var calledTools = new List<string>();
                        string? lastToolName = null;

                        using var activityTimeout = ActivityTimeout.Start(TimeSpan.FromSeconds(ExecutionLimits.Current.ActivityTimeoutSeconds), turnCts.Token);

                        if (_pendingCompaction)
                        {
                            messages.InsertRange(0, new[]
                            {
                                conversationHistory[0],  // already has the header
                                conversationHistory[1]   // Context restored. Ready to continue.
                            });
                            _pendingCompaction = false;
                        }

                        if (_pendingReseed)
                        {
                            // Batch6: replay trimmed history (everything before the current
                            // goal, already re-added at line ~598) into the fresh session.
                            if (conversationHistory.Count > 1)
                                messages.InsertRange(0, conversationHistory.Take(conversationHistory.Count - 1));
                            _pendingReseed = false;
                        }

                        // Auto-continue on finish_reason == length: when the model's response is cut
                        // off by the output/reasoning token cap (NOT a real stop), transparently
                        // re-invoke on the SAME session so it resumes where it left off, bounded by
                        // ExecutionLimits.MaxAutoContinuesPerTurn. This is why a long reasoning block
                        // or tool run could appear to "just stop" with no error.
                        int autoContinues = 0;
                        int maxAutoContinues = ExecutionLimits.Current.MaxAutoContinuesPerTurn;
                        Microsoft.Extensions.AI.ChatFinishReason? lastFinishReason;
                        // Mid-turn compaction (executionLimits.midTurnCompaction): tripped the moment an
                        // authoritative UsageContent frame reports the running token total has crossed the
                        // threshold, then serviced after the stream unwinds (a session swap mid-enumeration
                        // is unsafe). _disabled latches on for the rest of the turn if a compaction fails to
                        // get back under the threshold, so a summary that stays large cannot re-trip forever.
                        bool midTurnCompactPending = false;
                        bool midTurnCompactDisabled = false;
                        do
                        {
                        lastFinishReason = null;
                        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(messages, session)
                                           .WithCancellation(activityTimeout.Token))
                        {
                            activityTimeout.Ping();
                            if (update.FinishReason is { } fr) lastFinishReason = fr;

                            if (!string.IsNullOrEmpty(update.Text))
                            {
                                if (!currentlyStreaming)
                                {
                                    MuxConsole.BeginStreaming();
                                    currentlyStreaming = true;
                                    startedStreaming = true;
                                }

                                MuxConsole.WriteStream(update.Text);
                                responseText.Append(update.Text);
                                TickLiveTokens(update.Text.Length);

                                HookWorker.Enqueue(new HookEvent
                                {
                                    Event = "text_chunk",
                                    Agent = singleAgentDef.Name,
                                    Text = update.Text,
                                    Timestamp = DateTimeOffset.UtcNow
                                });
                            }

                            foreach (AIContent content in update.Contents)
                            {
                                if (content is TextReasoningContent reasoningContent)
                                {
                                    if (string.IsNullOrEmpty(reasoningContent.Text))
                                        continue;

                                    if (!currentlyStreaming)
                                    {
                                        thinking?.Dispose();
                                        thinking = null;
                                        MuxConsole.BeginStreaming();
                                        currentlyStreaming = true;
                                        startedStreaming = true;
                                    }

                                    MuxConsole.WriteStream(reasoningContent.Text, muted: true);
                                    TickLiveTokens(reasoningContent.Text.Length);

                                    HookWorker.Enqueue(new HookEvent
                                    {
                                        Event = "thinking_chunk",
                                        Agent = singleAgentDef.Name,
                                        Text = reasoningContent.Text,
                                        Timestamp = DateTimeOffset.UtcNow
                                    });

                                    continue;
                                }

                                if (content is FunctionCallContent functionCall)
                                {
                                    lastToolName = functionCall.Name;
                                    CostLedger.RecordToolCall(resolvedModelId);
                                    // NOTE: do NOT tick the live meter on tool-call name/args. They become part of
                                    // the provider's InputTokenCount on the next iteration's UsageContent frame, so
                                    // counting their chars here double-counts and inflates the estimate (the old
                                    // "drops on turn end" artifact). The authoritative usage frame moves the meter
                                    // (see ReconcileLiveBaseline); the thinking indicator shows tool work in progress.

                                    HookWorker.Enqueue(new HookEvent
                                    {
                                        Event = "tool_call",
                                        Agent = singleAgentDef.Name,
                                        Tool = functionCall.Name,
                                        Timestamp = DateTimeOffset.UtcNow
                                    });

                                    var toolSpan = OtelTracer.GetSource().StartActivity("tool_call");
                                    toolSpan?.SetTag("agent", singleAgentDef.Name);
                                    toolSpan?.SetTag("tool", functionCall.Name);
                                    toolSpan?.SetTag("args", functionCall.Arguments?.ToString()?[..Math.Min(functionCall.Arguments?.ToString()?.Length ?? 0, 4096)]);

                                    if (currentlyStreaming)
                                    {
                                        currentlyStreaming = false;
                                        thinking?.Dispose();
                                        thinking = MuxConsole.ResumeThinking(singleAgentDef.Name);
                                        calledTools.Add(functionCall.Name);
                                        thinking.UpdateStatus(calledTools);
                                    }
                                    else
                                    {
                                        calledTools.Add(functionCall.Name);
                                        thinking?.UpdateStatus(calledTools);
                                    }
                                }
                                else if (content is FunctionResultContent functionResult)
                                {
                                    var resultText = functionResult.Result?.ToString();
                                    // NOTE: do NOT tick the live meter on tool-result text -- like tool-call args it is
                                    // folded into the provider's InputTokenCount on the next iteration, so char-ticking
                                    // here double-counts. The authoritative UsageContent frame moves the meter.
                                    Activity.Current?.SetTag("success", true);
                                    if (resultText != null)
                                        Activity.Current?.SetTag("result", resultText.Length > 4096 ? resultText[..4096] : resultText);
                                    Activity.Current?.Stop();

                                    HookWorker.Enqueue(new HookEvent
                                    {
                                        Event = "tool_result",
                                        Agent = singleAgentDef.Name,
                                        Summary = functionResult.Result?.ToString(),
                                        Timestamp = DateTimeOffset.UtcNow
                                    });

                                    if (resultText != null)
                                        MuxConsole.WriteToolResult(singleAgentDef.Name, lastToolName ?? "unknown", resultText);

                                    if (!currentlyStreaming && thinking != null)
                                    {
                                        thinking.Dispose();
                                        thinking = MuxConsole.BeginThinking(singleAgentDef.Name);
                                        if (calledTools.Count > 0)
                                            thinking.UpdateStatus(calledTools);
                                    }
                                }
                                else if (content is UsageContent usageContent)
                                {
                                    var details = usageContent.Details;
                                    // Authoritative checkpoint: snap to the provider's real counts. A multi-iteration
                                    // tool turn emits one of these PER sub-call, so this is the ground truth the live
                                    // estimate extrapolates forward from. Only overwrite when the provider actually
                                    // reported a total (some intermediate frames carry only partial fields).
                                    if (details.TotalTokenCount is long total && total > 0)
                                        _sessionTokens = (uint)total;
                                    _cachedTokens = (uint)(details.CachedInputTokenCount ?? _cachedTokens);
                                    // Track cumulative input/output for the /cost gauge (authoritative running totals).
                                    if (details.InputTokenCount is long inTok && inTok > 0) _cumInputTokens = inTok;
                                    if (details.OutputTokenCount is long outTok && outTok > 0) _cumOutputTokens = outTok;
                                    // Reset the per-checkpoint char accumulator so the next live tick bridges only the
                                    // gap SINCE this checkpoint -- not the whole turn (kills the "drops on turn end"
                                    // artifact), and repaint the meter immediately with the real number.
                                    ReconcileLiveBaseline();
                                    RenderStatusBar();
                                    OtelMetrics.RecordTokens(
                                        singleAgentDef.Name, resolvedModelId, details.InputTokenCount ?? 0, details.OutputTokenCount ?? 0, details.CachedInputTokenCount, details.ReasoningTokenCount, details.TotalTokenCount
                                        );
                                    CostLedger.RecordUsage(resolvedModelId,
                                        details.InputTokenCount ?? 0, details.OutputTokenCount ?? 0,
                                        details.CachedInputTokenCount ?? 0, details.ReasoningTokenCount ?? 0,
                                        details.TotalTokenCount ?? 0);

                                    // Mid-turn compaction: this UsageContent frame is the authoritative token
                                    // checkpoint, so it is the earliest safe point to notice the session has grown
                                    // past the threshold WITHIN a turn. Flag it and stop consuming the stream; the
                                    // actual compaction (which swaps the session) runs once the enumeration unwinds.
                                    // Fires regardless of finish reason: the authoritative token total only lands
                                    // on the terminal frame (which often carries Stop), so gating on !Stop would
                                    // defer every peak-at-end crossing to the next turn and defeat mid-turn compaction.
                                    if (ExecutionLimits.Current.MidTurnCompaction
                                        && !midTurnCompactDisabled
                                        && autoCompactTokenThreshold is int mtcThreshold
                                        && _sessionTokens > mtcThreshold)
                                    {
                                        midTurnCompactPending = true;
                                        break;
                                    }
                                }
                            }
                        }

                        // Mid-turn compaction service point: the stream was stopped because the token
                        // threshold was crossed mid-turn. Compact now (TryCompactAsync summarizes the
                        // conversation, creates a FRESH session, and resets conversationHistory to the
                        // summary), then wipe the live message list clean and reseed only the compacted
                        // summary so the turn resumes on the shrunk context. The system prompt lives on
                        // the agent (not in `messages`), so it survives untouched. If the summary is
                        // still over threshold, latch mid-turn compaction off for the rest of the turn.
                        if (midTurnCompactPending)
                        {
                            midTurnCompactPending = false;

                            // Preserve any text streamed before the checkpoint so the summary sees it.
                            string mtcPartial = responseText.ToString();
                            if (!string.IsNullOrWhiteSpace(mtcPartial))
                                conversationHistory.Add(new ChatMessage(ChatRole.Assistant, mtcPartial));
                            responseText.Clear();

                            MuxConsole.WriteInfo($"Context crossed limit mid-turn (~{_sessionTokens:N0} tokens). Auto-compacting...");
                            bool mtcOk = await TryCompactAsync();

                            if (mtcOk)
                            {
                                // TryCompactAsync set _pendingCompaction and reset conversationHistory to
                                // [summary, "Context restored..."]. Rebuild the turn payload from scratch on
                                // the fresh session: the two reseed messages plus a nudge to continue.
                                _pendingCompaction = false;
                                messages.Clear();
                                messages.Add(conversationHistory[0]);
                                messages.Add(conversationHistory[1]);
                                messages.Add(new ChatMessage(ChatRole.User, "continue"));

                                // If the fresh summary still exceeds the threshold, do not let it re-trip every
                                // frame - run the rest of this turn without mid-turn compaction.
                                if (autoCompactTokenThreshold is int mtcAfter && _sessionTokens > mtcAfter)
                                {
                                    midTurnCompactDisabled = true;
                                    MuxConsole.WriteWarning("Compacted context still over threshold; mid-turn compaction paused for this turn.");
                                }

                                continue;
                            }

                            // Compaction unavailable (no compaction model): disable for the turn so we do
                            // not spin, and fall through to finish the turn normally.
                            midTurnCompactDisabled = true;
                        }

                        // If the stream ended because the output cap was hit (length) and we still
                        // have auto-continue budget, nudge the model to resume on the same session.
                        if (lastFinishReason == Microsoft.Extensions.AI.ChatFinishReason.Length
                            && autoContinues < maxAutoContinues)
                        {
                            autoContinues++;
                            messages.Clear();
                            messages.Add(new ChatMessage(ChatRole.User, "continue"));
                        }
                        else
                        {
                            if (lastFinishReason == Microsoft.Extensions.AI.ChatFinishReason.Length
                                && maxAutoContinues > 0)
                                MuxConsole.WriteMuted("[output cap reached after "
                                    + $"{autoContinues} auto-continue(s) — type 'continue' to resume]");
                            break;
                        }
                        }
                        while (true);
                    }
                    finally
                    {
                        if (currentlyStreaming)
                        {
                            try { MuxConsole.EndStreaming(); } catch { /* ignore */ }
                        }

                        StdinCancelMonitor.Instance?.ClearActiveTurnCts();
                        thinking?.Dispose();

                        HookWorker.Enqueue(new HookEvent
                        {
                            Event = "turn_end",
                            Agent = singleAgentDef.Name,
                            Summary = responseText.Length > 500 ? responseText.ToString(0, 500) + "..." : responseText.ToString(),
                            Timestamp = DateTimeOffset.UtcNow
                        });

                        turnSw.Stop();
                        OtelMetrics.AgentTurns.Add(1, new KeyValuePair<string, object?>("agent", singleAgentDef.Name));
                        OtelMetrics.AgentTurnDuration.Record(turnSw.ElapsedMilliseconds,
                            new KeyValuePair<string, object?>("agent", singleAgentDef.Name));

                        // Only Fires In Verbose Path
                        OtelMetrics.RecordAgentMessage(singleAgentDef.Name, "assistant", responseText.ToString());
                    }

                    MuxConsole.WriteAgentTurnFooter();

                    string response = responseText.ToString();

                    if (!string.IsNullOrWhiteSpace(response))
                        conversationHistory.Add(new ChatMessage(ChatRole.Assistant, response));

                    if (string.IsNullOrWhiteSpace(response))
                    {
                        stuckCount++;
                        MuxConsole.WriteWarning($"Empty response ({stuckCount}/3)");
                        OtelMetrics.AgentStuckCount.Add(1, new KeyValuePair<string, object?>("agent", singleAgentDef.Name));
                        if (stuckCount >= 3)
                        {
                            MuxConsole.WriteError("Stuck repeatedly — aborting turn.");
                            break;
                        }

                        messages.Clear();
                        messages.Add(new ChatMessage(ChatRole.User,
                            "Your last response was empty. Please continue or summarize where you are."));
                        continue;
                    }

                    break;
                }
            }
            catch (OperationCanceledException) when (turnCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                wasInterrupted = true;

                string partial = responseText.ToString();
                string interruptedAssistant = string.IsNullOrWhiteSpace(partial)
                    ? "[no response — interrupted before agent replied]"
                    : partial + "\r\n\r\n[interrupted by user]";

                // The framework does NOT commit a cancelled run's messages to the session,
                // so neither the user goal nor the partial response would survive
                // persistence/resume. Inject both into the session's in-memory history
                // (preserving prior tool-rich turns) so the interrupted exchange is durable.
                if (!session.TryGetInMemoryChatHistory(out var sessionHistory) || sessionHistory is null)
                    sessionHistory = new List<ChatMessage>();
                sessionHistory.Add(new ChatMessage(ChatRole.User, currentGoal));
                sessionHistory.Add(new ChatMessage(ChatRole.Assistant, interruptedAssistant));
                session.SetInMemoryChatHistory(sessionHistory);

                // conversationHistory feeds compaction; keep the partial there too.
                if (!string.IsNullOrWhiteSpace(partial))
                    conversationHistory.Add(new ChatMessage(ChatRole.Assistant, partial + "\r\n\r\n[interrupted by user]"));

                MuxConsole.WriteLine();
                MuxConsole.WriteWarning("Turn cancelled by user (Esc key pressed).");
            }
            catch (Exception ex)
            {
                MuxConsole.WriteError(ex.Message);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (wasInterrupted)
                MuxConsole.WriteInfo("Ready for next input.");

            bool shouldPersist = persistSession;
            if (shouldPersist && persistIntervalSeconds > 0 && !wasInterrupted)
                shouldPersist = (DateTime.UtcNow - lastPersistTime).TotalSeconds >= persistIntervalSeconds;

            if (shouldPersist)
            {
                await Common.PersistChatSessionAsync(
                    agent,
                    session,
                    sessionTimestamp,
                    resumedSession.HasValue ? Common.FindSessionDirectory(sessionTimestamp) : null);

                lastPersistTime = DateTime.UtcNow;
            }

            // In TUI mode the docked footer already shows live token usage, so skip this
            // redundant per-turn line; classic mode keeps it.
            if (!MuxConsole.IsTui)
                MuxConsole.WriteMuted($"Total Tokens: {_sessionTokens:N0} - Cached Tokens: {_cachedTokens:N0}");

            //Cleanly Exit as goal was passed through cli args and continuous flag was not passed
            if (incomingGoal != null && !continuous)
                break;

            if (continuous && wasInterrupted)
            {
                MuxConsole.WriteSuccess("Continuous execution stopped by user.");
                break;
            }

            if (continuous)
            {
                if (minDelaySeconds > 0)
                {
                    using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    using var delayEsc = EscapeKeyListener.Start(delayCts, cancellationToken);

                    try
                    {
                        await MuxConsole.WithSpinnerAsync($"Next iteration in {minDelaySeconds}s, press [ESC] to cancel", async () =>
                        {
                            await Task.Delay(TimeSpan.FromSeconds(minDelaySeconds), delayCts.Token);
                        });
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        MuxConsole.WriteSuccess("Continuous execution stopped by user.");
                        break;
                    }
                }

                currentGoal = "Continue working on the task. If complete, summarize your results.";
                continue;
            }

            RenderStatusBar();
            // In TUI the pinned footer/input box separate turns; the classic rule + inline
            // "> " prompt would inject a duplicate separator line, so emit them only in classic.
            if (!MuxConsole.TuiActive)
            {
                MuxConsole.WriteRule();
                MuxConsole.WriteInline($"[{MuxConsole.PromptColor}]> [/]", "> ");
            }

            cancellationToken.ThrowIfCancellationRequested();
            string? nextInput = MuxConsole.ReadInput(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (IsQuitCommand(nextInput))
            {
                MuxConsole.WriteSuccess("Exited from Chat interface successfully!");
                break;
            }

            // Batch6: in-session meta-command loop. These commands act on the live
            // session without quitting it and without counting as a goal; loop until
            // a real message arrives (or the user quits).
            bool quitSession = false;
            bool retryGoalSet = false;
            while (true)
            {
                string metaCmd = nextInput!.Trim();
                // !<command>: run a shell command, show its output, and inject "[ran: <cmd>]\n<output>"
                // as the next user turn so the model sees the result. Intercepted before slash dispatch.
                if (metaCmd.StartsWith("!") && metaCmd.Length > 1)
                {
                    string shellCmd = metaCmd[1..].Trim();
                    if (shellCmd.Length == 0) { MuxConsole.WriteMuted("Usage: !<command>"); }
                    else
                    {
                        ShellCapture.Result r = await ShellCapture.RunAsync(
                            shellCmd, PlatformContext.WorkspaceRoot, 60, 60_000, cancellationToken);
                        string output = r.Combined;
                        if (string.IsNullOrWhiteSpace(output)) output = "(no output)";
                        MuxConsole.WritePanel($"! {shellCmd}", output);
                        currentGoal = $"[ran: {shellCmd}]\n{output}";
                        retryGoalSet = true;
                        break;
                    }
                }
                else if (metaCmd.Equals("/compact", StringComparison.OrdinalIgnoreCase)
                      || metaCmd.StartsWith("/compact ", StringComparison.OrdinalIgnoreCase))
                {
                    var cParts = metaCmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    var cInstruction = cParts.Length > 1 ? cParts[1].Trim() : null;
                    await TryCompactAsync(cInstruction);
                }
                else if (metaCmd.Equals("/handoff", StringComparison.OrdinalIgnoreCase)
                      || metaCmd.StartsWith("/handoff ", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleHandoffAsync(metaCmd, client, conversationHistory, compactionChatOptions);
                }
                else if (metaCmd.Equals("/heal", StringComparison.OrdinalIgnoreCase)
                      || metaCmd.StartsWith("/heal ", StringComparison.OrdinalIgnoreCase)
                      || metaCmd.Equals("/reflect", StringComparison.OrdinalIgnoreCase)
                      || metaCmd.StartsWith("/reflect ", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleHealAsync(metaCmd, client, conversationHistory,
                        resolvedModelId, chatClientFactory, compactionChatOptions, cancellationToken);
                }
                else if (metaCmd.Equals("/fix", StringComparison.OrdinalIgnoreCase)
                      || metaCmd.StartsWith("/fix ", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleFixAsync(metaCmd, client, compactionChatOptions, cancellationToken);
                }
                else if (metaCmd.Equals("/diff", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleDiffAsync(cancellationToken);
                }
                else if (metaCmd.Equals("/doctor", StringComparison.OrdinalIgnoreCase))
                {
                    HandleDoctor();
                }
                else if (metaCmd.Equals("/cost", StringComparison.OrdinalIgnoreCase)
                      || metaCmd.StartsWith("/cost ", StringComparison.OrdinalIgnoreCase))
                {
                    var costArg = metaCmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (costArg.Length > 1 && costArg[1].Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
                        HandleCostBreakdown(resolvedModelId);
                    else
                        HandleCost(resolvedModelId);
                }
                else if (metaCmd.Equals("/init", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleInitAsync(client, compactionChatOptions, cancellationToken);
                }
                else if (metaCmd.Equals("/review", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleReviewAsync(client, compactionChatOptions, cancellationToken);
                }
                else if (metaCmd.Equals("/wipe", StringComparison.OrdinalIgnoreCase))
                {
                    session = await agent.CreateSessionAsync();
                    conversationHistory.Clear();
                    _sessionTokens = 0;
                    _cachedTokens = 0;
                    _cumInputTokens = 0;
                    _cumOutputTokens = 0;
                    _pendingCompaction = false;
                    _pendingReseed = false;
                    CostLedger.ResetSession();
                    ServeMode.EmitEvent(new { type = "session_wiped" });
                    MuxConsole.WriteSuccess("Session context wiped. Starting fresh.");
                }
                else if (metaCmd.Equals("/tokens", StringComparison.OrdinalIgnoreCase)
                      || metaCmd.StartsWith("/tokens ", StringComparison.OrdinalIgnoreCase)
                      || metaCmd.Equals("/context", StringComparison.OrdinalIgnoreCase)
                      || metaCmd.StartsWith("/context ", StringComparison.OrdinalIgnoreCase))
                {
                    var tokArg = metaCmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (tokArg.Length > 1 && tokArg[1].Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
                        HandleCostBreakdown(resolvedModelId);
                    else
                        PrintTokenUsage();
                }
                else if (metaCmd == "/" || metaCmd == "/?")
                {
                    // G6: slash-command palette / preview (TUI render mode).
                    MuxConsole.RenderTuiSlashPalette();
                }
                else if (metaCmd.Equals("/undo", StringComparison.OrdinalIgnoreCase))
                {
                    await TryUndoAsync();
                }
                else if (metaCmd.Equals("/retry", StringComparison.OrdinalIgnoreCase)
                      || metaCmd.Equals("/redo", StringComparison.OrdinalIgnoreCase))
                {
                    var retryText = TryPrepareRetry();
                    if (retryText != null)
                    {
                        await RebuildSessionFromHistoryAsync();
                        currentGoal = retryText;
                        retryGoalSet = true;
                        MuxConsole.WriteInfo("Retrying last message...");
                        break;
                    }
                }
                else if (metaCmd.Equals("/effort", StringComparison.OrdinalIgnoreCase)
                      || metaCmd.StartsWith("/effort ", StringComparison.OrdinalIgnoreCase))
                {
                    // "/effort" with no arg cycles; "/effort <tier>" sets explicitly.
                    var parts = metaCmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 1) SetEffortByName(parts[1]);
                    else
                    {
                        var tier = CycleEffort();
                        MuxConsole.SetTuiEffort(tier);
                        MuxConsole.WriteSuccess($"Reasoning effort set to {tier}.");
                    }
                }
                else if (metaCmd.Equals("/tag", StringComparison.OrdinalIgnoreCase)
                      || metaCmd.StartsWith("/tag ", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleTagAsync(metaCmd, sessionTimestamp, session, agent,
                        resolvedModelId, chatClientFactory, cancellationToken);
                }
                else if (metaCmd.Equals("/kanban", StringComparison.OrdinalIgnoreCase)
                      || metaCmd.StartsWith("/kanban ", StringComparison.OrdinalIgnoreCase))
                {
                    // Editable team board for the active taskboard team; a no-op hint off-team.
                    MuxSwarm.Utils.Teams.KanbanCommand.Run(metaCmd);
                }
                else if (metaCmd.Equals("/voice", StringComparison.OrdinalIgnoreCase)
                      || metaCmd.StartsWith("/voice ", StringComparison.OrdinalIgnoreCase))
                {
                    // Voice dictation toggle (whisper.cpp). Session-agnostic app-wide mode; the
                    // TUI compose caret becomes the live state dot while active.
                    CliCmdUtils.HandleVoice(metaCmd);
                }
                else if (metaCmd.Equals("/background", StringComparison.OrdinalIgnoreCase) || metaCmd.Equals("/bg", StringComparison.OrdinalIgnoreCase)
                      || metaCmd.StartsWith("/background ", StringComparison.OrdinalIgnoreCase) || metaCmd.StartsWith("/bg ", StringComparison.OrdinalIgnoreCase))
                {
                    // Launch/list/cancel background agent jobs (watchable via the \\ Agent View).
                    await DetachedRunner.RunCommand(metaCmd, chatClientFactory, Models, cancellationToken);
                }
                else if (metaCmd.Equals("/daemon", StringComparison.OrdinalIgnoreCase) || metaCmd.Equals("/da", StringComparison.OrdinalIgnoreCase)
                      || metaCmd.StartsWith("/daemon ", StringComparison.OrdinalIgnoreCase) || metaCmd.StartsWith("/da ", StringComparison.OrdinalIgnoreCase))
                {
                    // Runtime control of the in-house daemon (cron/watch/on/off/jobs/cancel).
                    MuxSwarm.State.DaemonCommand.Run(metaCmd);
                }
                else if (metaCmd.Equals("/update", StringComparison.OrdinalIgnoreCase))
                {
                    // Process-level self-update (Scope.Both), identical to the menu path: pull the
                    // latest release, verify its SHA256, replace changed shipped files (user configs/
                    // sessions/memory preserved), and relaunch if the binary itself changed.
                    var (staged, umsg) = await MuxSwarm.State.SelfUpdater.RunAsync(line => MuxConsole.WriteInfo(line), cancellationToken);
                    MuxConsole.WriteInfo(umsg);
                    if (staged)
                    {
                        MuxConsole.WriteWarning("Mux-Swarm must restart to finish applying the update. Restarting...");
                        MuxSwarm.State.Relauncher.RestartNow(() => MuxConsole.DisableDockedFooter());
                    }
                }
                else if (metaCmd.Equals("/detach", StringComparison.OrdinalIgnoreCase))
                {
                    // v0.12.0 live-session detach: park THIS interactive session in the background
                    // and return to the top-level menu, re-attachable via /attach <id> or the \
                    // picker. The async frame stays alive across the await, preserving the whole
                    // session closure (agent, in-memory history, tokens) - no disk round-trip. Only
                    // valid at the idle prompt (we are between turns here by construction). When no
                    // handle was supplied (e.g. a daemon-fired or non-detachable launch) it is a
                    // clear no-op so /detach is never silently swallowed.
                    if (interactiveHandle is null)
                    {
                        MuxConsole.WriteMuted("This session cannot be detached. Use /qc to exit.");
                    }
                    else
                    {
                        // Persist so the parked session is also resumable from disk as a safety net.
                        try { await Common.PersistChatSessionAsync(agent, session, sessionTimestamp); }
                        catch { /* best-effort; the live frame is preserved regardless */ }
                        interactiveHandle.Tokens = _sessionTokens;
                        MuxConsole.WriteSuccess($"Detached session {interactiveHandle.Id} ({interactiveHandle.Label}). Re-attach with /attach {interactiveHandle.Id} or \\.");
                        // Release the session-scoped TUI hooks so the menu's footer is clean while
                        // parked; they are re-asserted on resume below.
                        MuxConsole.SetTuiSessionId(null);
                        // Park: hand the console back to the menu and block (async) until /attach.
                        await interactiveHandle.ParkAndAwaitAttachAsync(cancellationToken);
                        // --- resumed ---
                        MuxConsole.SetTuiSessionId(sessionTimestamp);
                        RenderStatusBar();
                        MuxConsole.WriteMuted($"\u21bb Resumed session {interactiveHandle.Id} ({interactiveHandle.Label}).");
                    }
                }
                else if (Tui.TuiCommands.IsReplOnly(metaCmd.Split(' ', 2)[0]))
                {
                    // Slash-anywhere (web-UI parity): the user typed a REPL-only command inside a
                    // live session, where it does NOT work. Offer to end the session and run it at
                    // the top-level menu. If they decline, stay in the session (treat as no-op).
                    string cmdName = metaCmd.Split(' ', 2)[0];
                    bool end = MuxConsole.Confirm(
                        $"'{cmdName}' only runs at the main menu. End this session and run it there?", false);
                    if (end)
                    {
                        PendingReplCommand = metaCmd;
                        quitSession = true;
                        break;
                    }
                    // declined: fall through to re-prompt (do not send the slash text to the agent)
                }
                else
                {
                    break; // real message (or unknown) -> fall through to goal handling
                }

                // Re-prompt after handling a meta command.
                RenderStatusBar();
                if (!MuxConsole.TuiActive)
                {
                    MuxConsole.WriteRule();
                    MuxConsole.WriteInline($"[{MuxConsole.PromptColor}]> [/]", "> ");
                }

                cancellationToken.ThrowIfCancellationRequested();
                nextInput = MuxConsole.ReadInput(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                if (IsQuitCommand(nextInput))
                {
                    MuxConsole.WriteSuccess("Exited from Chat interface successfully!");
                    quitSession = true;
                    break;
                }
            }

            if (quitSession)
                break;

            if (!retryGoalSet)
                currentGoal = nextInput!;

            HookWorker.Enqueue(new HookEvent
            {
                Event = "user_input",
                Agent = singleAgentDef.Name,
                Text = currentGoal,
                Timestamp = DateTimeOffset.UtcNow
            });

            OtelMetrics.RecordAgentMessage(singleAgentDef.Name, "user", currentGoal);
            OtelMetrics.GoalsReceived.Add(1, new KeyValuePair<string, object?>("agent", singleAgentDef.Name));

        } while (!Environment.HasShutdownStarted);

        // Clear the session-scoped TUI hooks so they do not leak to the top-level menu.
        MuxConsole.SetTuiModeCycle(null);
        MuxConsole.SetTuiEffort(null);
        MuxConsole.SetTuiSessionId(null);

        if (sessionRetention > 0)
            Common.PruneOldSessions(PlatformContext.SessionsDirectory, sessionRetention);

        HookWorker.Enqueue(new HookEvent
        {
            Event = "session_end",
            Agent = singleAgentDef.Name,
            Summary = cancellationToken.IsCancellationRequested ? "interrupted" : "complete",
            Timestamp = DateTimeOffset.UtcNow
        });

        sessionSpan?.SetTag("outcome", cancellationToken.IsCancellationRequested ? "interrupted" : "complete");
        sessionSpan?.SetTag("final_tokens", _sessionTokens);

        if (cancellationToken.IsCancellationRequested)
            OtelMetrics.SessionsFailed.Add(1, new KeyValuePair<string, object?>("agent", singleAgentDef.Name));
        else
            OtelMetrics.SessionsCompleted.Add(1, new KeyValuePair<string, object?>("agent", singleAgentDef.Name));
    }
}