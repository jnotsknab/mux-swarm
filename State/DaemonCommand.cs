using Microsoft.Extensions.AI;
using MuxSwarm.Utils;

namespace MuxSwarm.State;

/// <summary>
/// The <c>/daemon</c> (alias <c>/da</c>) in-session command family: runtime control over the
/// in-house daemon (<see cref="DaemonRunner"/>) without restarting the process. Mirrors the
/// <c>/kanban</c> subcommand pattern.
///
///   /daemon on | off            start / stop the daemon (boot triggers from config.json)
///   /daemon jobs | list         list active triggers (boot + runtime) and detached jobs
///   /daemon cron "&lt;expr&gt;" &lt;mode&gt; &lt;goal&gt;   add a cron trigger at runtime
///   /daemon watch &lt;glob&gt; &lt;mode&gt; &lt;goal&gt;       add a file-watch trigger at runtime
///   /daemon cancel &lt;id&gt;         cancel a runtime trigger
///
/// When <c>cron</c>/<c>watch</c> are invoked with missing or partial arguments, an interactive
/// wizard walks the user through building the trigger (mirrors <c>/newagent</c> and
/// <c>/createhook</c>). The cron wizard accepts a natural-language schedule ("every weekday at
/// 9am") and translates it to a 5-field cron via <see cref="CronNaturalLanguage"/> - a raw cron
/// expression is always accepted verbatim as the fallback. The mode step lets the user pick
/// agent|swarm|pswarm, and for agent mode optionally names a specific agent (also expressible on
/// the raw line as an <c>agent:Name</c> mode token).
///
/// Runtime triggers can optionally be persisted to config.json (the user is prompted), so they
/// survive a restart. Boot triggers are unaffected.
/// </summary>
public static class DaemonCommand
{
    /// <summary>Ensure a started DaemonRunner exists (lazily create + Start), or null on failure.</summary>
    private static DaemonRunner? EnsureRunner()
    {
        if (App.DaemonRunner is { IsStarted: true } r) return r;
        try
        {
            App.Config.Daemon ??= new DaemonConfig();
            App.DaemonRunner ??= new DaemonRunner(App.Config.Daemon);
            App.DaemonRunner.Start(
                chatClientFactory: modelId => App.CreateChatClient(modelId),
                mcpTools: App.GetMcpTools()!.Cast<AITool>().ToList(),
                agentModels: Common.LoadAgentModels());
            return App.DaemonRunner;
        }
        catch (System.Exception ex)
        {
            MuxConsole.WriteWarning($"[daemon] Could not start: {ex.Message}");
            return null;
        }
    }

    public static void Run(string raw)
    {
        var line = (raw ?? "").Trim();
        foreach (var pfx in new[] { "/daemon", "/da" })
            if (line.StartsWith(pfx, System.StringComparison.OrdinalIgnoreCase))
            { line = line.Substring(pfx.Length).Trim(); break; }

        if (line.Length == 0 || line.Equals("jobs", System.StringComparison.OrdinalIgnoreCase)
            || line.Equals("list", System.StringComparison.OrdinalIgnoreCase)
            || line.Equals("status", System.StringComparison.OrdinalIgnoreCase))
        {
            RenderStatus();
            return;
        }

        var parts = line.Split(' ', 2, System.StringSplitOptions.RemoveEmptyEntries);
        var verb = parts[0].ToLowerInvariant();
        var rest = parts.Length > 1 ? parts[1].Trim() : "";

        switch (verb)
        {
            case "on": case "start":
                MuxConsole.WriteMuted(EnsureRunner() is not null ? "[daemon] Running." : "[daemon] Failed to start.");
                return;
            case "off": case "stop":
                if (App.DaemonRunner is not null)
                {
                    _ = App.DaemonRunner.DisposeAsync();
                    App.DaemonRunner = null;
                    MuxConsole.WriteSuccess("[daemon] Stopped.");
                }
                else MuxConsole.WriteMuted("[daemon] Not running.");
                return;
            case "cron":  AddCron(rest); return;
            case "watch": AddWatch(rest); return;
            case "cancel": case "kill":
                if (rest.Length == 0) { MuxConsole.WriteWarning("Usage: /daemon cancel <id>"); return; }
                MuxConsole.WriteMuted(App.DaemonRunner?.CancelTrigger(rest) == true
                    ? $"[daemon] Cancelled {rest}." : $"[daemon] No runtime trigger '{rest}'.");
                return;
            case "help": case "?":
                foreach (var h in HelpLines) MuxConsole.WriteMuted(h);
                return;
            default:
                MuxConsole.WriteWarning($"Unknown /daemon action '{verb}'. Try /daemon help.");
                return;
        }
    }

    // /daemon cron "<expr>" <mode> <goal...>   (mode may be agent|swarm|pswarm or agent:Name)
    // With missing/partial args, drops into the interactive builder.
    private static void AddCron(string rest)
    {
        var (expr, after) = TakeQuotedOrToken(rest);
        var mp = after.Split(' ', 2, System.StringSplitOptions.RemoveEmptyEntries);

        // Fully-specified fast path: raw expr + mode + goal, all present and the expr parses.
        if (expr.Length > 0 && mp.Length >= 2 && CronExpression.Parse(expr) is not null)
        {
            var (mode0, agent0) = ParseModeToken(mp[0]);
            var trig0 = new DaemonTrigger { Type = "cron", Schedule = expr, Mode = mode0, Goal = mp[1].Trim() };
            if (mode0 == "agent") trig0.Agent = agent0;
            Commit(trig0, DescribeCron(trig0));
            return;
        }

        // Interactive builder.
        MuxConsole.WriteInfo("[daemon] Building a cron trigger. Describe the schedule in plain English " +
                             "(e.g. \"every weekday at 9am\", \"every 5 minutes\") or paste a raw 5-field cron.");
        string phrase = expr.Length > 0 ? expr : MuxConsole.Prompt("Schedule").Trim();
        if (phrase.Length == 0) { MuxConsole.WriteWarning("[daemon] Cancelled (no schedule)."); return; }

        string? cron = ResolveCronInteractive(phrase);
        if (cron is null) { MuxConsole.WriteWarning("[daemon] Cancelled (no valid cron expression)."); return; }

        var (mode, agent) = PromptMode(mp.Length >= 1 ? mp[0] : null);
        string goal = mp.Length >= 2 ? mp[1].Trim() : MuxConsole.Prompt("Goal (what should run)").Trim();
        if (goal.Length == 0) { MuxConsole.WriteWarning("[daemon] Cancelled (no goal)."); return; }

        var trig = new DaemonTrigger { Type = "cron", Schedule = cron, Mode = mode, Goal = goal };
        if (mode == "agent") trig.Agent = agent;
        Commit(trig, DescribeCron(trig));
    }

    // /daemon watch <glob> <mode> <goal...>    (mode may be agent|swarm|pswarm or agent:Name)
    private static void AddWatch(string rest)
    {
        var parts = rest.Split(' ', 3, System.StringSplitOptions.RemoveEmptyEntries);

        // Fully-specified fast path.
        if (parts.Length >= 3)
        {
            var (mode0, agent0) = ParseModeToken(parts[1]);
            var t0 = new DaemonTrigger { Type = "watch", Path = parts[0].Trim(), Mode = mode0, Goal = parts[2].Trim() };
            if (mode0 == "agent") t0.Agent = agent0;
            Commit(t0, DescribeWatch(t0));
            return;
        }

        // Interactive builder.
        MuxConsole.WriteInfo("[daemon] Building a file-watch trigger.");
        string glob = parts.Length >= 1 ? parts[0].Trim()
            : MuxConsole.Prompt("Path glob to watch (e.g. src/**/*.cs)").Trim();
        if (glob.Length == 0) { MuxConsole.WriteWarning("[daemon] Cancelled (no path)."); return; }

        var (mode, agent) = PromptMode(parts.Length >= 2 ? parts[1] : null);
        string goal = MuxConsole.Prompt("Goal (runs on change; {file} = changed path)").Trim();
        if (goal.Length == 0) { MuxConsole.WriteWarning("[daemon] Cancelled (no goal)."); return; }

        var trig = new DaemonTrigger { Type = "watch", Path = glob, Mode = mode, Goal = goal };
        if (mode == "agent") trig.Agent = agent;
        Commit(trig, DescribeWatch(trig));
    }

    /// <summary>
    /// Resolve a schedule phrase to a validated cron expression, interactively. Tries the raw +
    /// deterministic-pattern paths first (no model call); on a miss, offers a one-shot LLM
    /// translation, shows the result for confirmation, and falls back to asking for a raw cron.
    /// Returns null if the user abandons.
    /// </summary>
    private static string? ResolveCronInteractive(string phrase)
    {
        var (det, src) = CronNaturalLanguage.ResolveDeterministic(phrase);
        if (det is not null)
        {
            if (src == CronNaturalLanguage.Source.Raw) return det;
            MuxConsole.WriteMuted($"[daemon] Interpreted \"{phrase}\" as cron: {det}  ({CronExpression.Describe(det)})");
            return MuxConsole.Confirm("Use this schedule?", true) ? det : PromptRawCron();
        }

        // Deterministic miss: offer the light-model translator.
        if (MuxConsole.Confirm($"Could not parse \"{phrase}\" directly. Ask a model to translate it to cron?", true))
        {
            var (client, opts) = ResolveLightClient();
            if (client is not null)
            {
                string? llm = null;
                try
                {
                    llm = CronNaturalLanguage.ResolveAsync(phrase, client, opts, System.Threading.CancellationToken.None)
                        .GetAwaiter().GetResult().Cron;
                }
                catch (System.Exception ex) { MuxConsole.WriteWarning($"[daemon] Translation failed: {ex.Message}"); }

                if (llm is not null)
                {
                    MuxConsole.WriteMuted($"[daemon] Model suggests cron: {llm}  ({CronExpression.Describe(llm)})");
                    if (MuxConsole.Confirm("Use this schedule?", true)) return llm;
                }
                else MuxConsole.WriteWarning("[daemon] The model did not return a valid cron expression.");
            }
            else MuxConsole.WriteWarning("[daemon] No model available for translation.");
        }

        return PromptRawCron();
    }

    // Last-resort: ask for a raw 5-field cron and validate it.
    private static string? PromptRawCron()
    {
        var raw = MuxConsole.Prompt("Enter a raw 5-field cron expression (or blank to cancel)").Trim();
        if (raw.Length == 0) return null;
        if (CronExpression.Parse(raw) is null) { MuxConsole.WriteWarning($"[daemon] Invalid cron: {raw}"); return null; }
        return raw;
    }

    // Prompt for the orchestration mode; when "agent", optionally name a specific agent.
    // A pre-supplied token (from the raw line) is honored without prompting when valid.
    private static (string Mode, string? Agent) PromptMode(string? preset)
    {
        if (!string.IsNullOrWhiteSpace(preset))
        {
            var (m, a) = ParseModeToken(preset!);
            if (m is "swarm" or "pswarm") return (m, null);
            if (m == "agent" && a is not null) return ("agent", a);
            // bare "agent" preset falls through to the agent picker below.
            if (m == "agent") return ("agent", PickAgent());
        }

        var mode = MuxConsole.Select("Orchestration mode", new[] { "agent", "swarm", "pswarm" });
        return mode == "agent" ? ("agent", PickAgent()) : (mode, null);
    }

    // Let the user pick a specific agent for single-agent mode (or the default single agent).
    private static string? PickAgent()
    {
        var names = Common.DelegableAgents().Select(a => a.Name).ToList();
        if (names.Count == 0) return null;
        var choices = new List<string> { "(default single agent)" };
        choices.AddRange(names);
        var pick = MuxConsole.Select("Which agent should run the goal?", choices);
        return pick.StartsWith("(default") ? null : pick;
    }

    // Parse a mode token: "agent" | "swarm" | "pswarm" | "agent:Name". Unknown -> agent.
    private static (string Mode, string? Agent) ParseModeToken(string token)
    {
        var t = (token ?? "").Trim();
        if (t.Contains(':'))
        {
            var sp = t.Split(':', 2);
            var head = sp[0].Trim().ToLowerInvariant();
            var name = sp[1].Trim();
            if (head == "agent" && name.Length > 0)
            {
                // Resolve against the roster case-insensitively; keep the user's text if unmatched.
                var match = Common.DelegableAgents().Select(a => a.Name)
                    .FirstOrDefault(n => n.Equals(name, System.StringComparison.OrdinalIgnoreCase));
                return ("agent", match ?? name);
            }
        }
        var v = t.ToLowerInvariant();
        return (v is "swarm" or "pswarm" or "agent" ? v : "agent", null);
    }

    // Resolve a LIGHT chat client for cron translation: compaction model -> orchestrator model.
    private static (IChatClient? Client, ChatOptions? Options) ResolveLightClient()
    {
        try
        {
            var models = Common.LoadAgentModels();
            string? modelId = MultiAgentOrchestrator.SwarmConfig?.CompactionAgent?.Model;
            if (string.IsNullOrWhiteSpace(modelId))
                modelId = models.TryGetValue("Orchestrator", out var om) ? om : null;
            if (string.IsNullOrWhiteSpace(modelId)) return (null, null);

            var opts = MultiAgentOrchestrator.SwarmConfig?.CompactionAgent?.ModelOpts?.ToChatOptions();
            return (App.CreateChatClient(modelId!), opts);
        }
        catch { return (null, null); }
    }

    private static void Commit(DaemonTrigger trig, string label)
    {
        var runner = EnsureRunner();
        if (runner is null) return;
        var id = runner.AddTriggerRuntime(trig);
        if (id is null) return;
        MuxConsole.WriteSuccess($"[daemon] Added {id}: {label}.");

        // Offer to persist so it survives a restart.
        if (MuxConsole.Confirm("Persist this trigger to config.json (survives restart)?", false))
        {
            try
            {
                App.Config.Daemon ??= new DaemonConfig();
                App.Config.Daemon.Enabled = true;
                App.Config.Daemon.Triggers.Add(trig);
                Common.SaveConfig(App.Config);
                MuxConsole.WriteSuccess("[daemon] Saved to config.json.");
            }
            catch (System.Exception ex) { MuxConsole.WriteWarning($"[daemon] Persist failed: {ex.Message}"); }
        }
        else MuxConsole.WriteMuted("[daemon] Ephemeral (gone on restart).");
    }

    private static string DescribeCron(DaemonTrigger t)
    {
        string who = t.Mode == "agent" && t.Agent is not null ? $"{t.Mode}:{t.Agent}" : t.Mode;
        return $"cron {t.Schedule} ({CronExpression.Describe(t.Schedule ?? "")}) -> {who}";
    }

    private static string DescribeWatch(DaemonTrigger t)
    {
        string who = t.Mode == "agent" && t.Agent is not null ? $"{t.Mode}:{t.Agent}" : t.Mode;
        return $"watch {t.Path} -> {who}";
    }

    private static void RenderStatus()
    {
        var sb = new System.Text.StringBuilder();
        bool running = App.DaemonRunner is { IsStarted: true };
        sb.AppendLine($"Daemon: {(running ? "running" : "stopped")}");
        if (running)
        {
            var triggers = App.DaemonRunner!.ListTriggers();
            if (triggers.Count == 0) sb.AppendLine("  (no triggers)");
            foreach (var (id, type, detail, runtime) in triggers)
                sb.AppendLine($"  {id} [{type}{(runtime ? ",runtime" : "")}] {detail}");
        }
        var jobs = DetachedRunner.Jobs();
        if (jobs.Count > 0)
        {
            sb.AppendLine($"Detached jobs ({DetachedRunner.RunningCount()} running):");
            sb.Append(DetachedRunner.Render());
        }
        MuxConsole.WritePanel("Daemon", sb.ToString());
    }

    // Pull a leading "quoted string" or a single token off the front; return (token, remainder).
    private static (string Token, string Remainder) TakeQuotedOrToken(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return ("", "");
        if (s[0] == '"')
        {
            int end = s.IndexOf('"', 1);
            if (end > 0) return (s.Substring(1, end - 1), s[(end + 1)..].Trim());
        }
        var sp = s.Split(' ', 2, System.StringSplitOptions.RemoveEmptyEntries);
        return (sp[0], sp.Length > 1 ? sp[1].Trim() : "");
    }

    private static readonly string[] HelpLines =
    {
        "/daemon (alias /da) - runtime control of the in-house daemon.",
        "  /daemon on | off                       start / stop the daemon",
        "  /daemon jobs                           list triggers + detached jobs",
        "  /daemon cron [\"<expr|phrase>\"] [mode] [goal]   add a cron trigger (bare = interactive builder)",
        "  /daemon watch [<glob>] [mode] [goal]   add a file-watch trigger (bare = interactive builder)",
        "  /daemon cancel <id>                    cancel a runtime trigger",
        "    mode: agent | swarm | pswarm | agent:<Name>  (agent:<Name> pins a specific agent)",
        "    cron schedule accepts plain English (\"every weekday at 9am\") or a raw 5-field cron.",
    };
}
