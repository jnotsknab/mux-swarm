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

    // /daemon cron "<expr>" <mode> <goal...>
    private static void AddCron(string rest)
    {
        var (expr, after) = TakeQuotedOrToken(rest);
        if (expr.Length == 0) { MuxConsole.WriteWarning("Usage: /daemon cron \"<cron expr>\" <mode> <goal>"); return; }
        var mp = after.Split(' ', 2, System.StringSplitOptions.RemoveEmptyEntries);
        if (mp.Length < 2) { MuxConsole.WriteWarning("Usage: /daemon cron \"<cron expr>\" <agent|swarm|pswarm> <goal>"); return; }
        var mode = NormalizeMode(mp[0]);
        var goal = mp[1].Trim();
        if (CronExpression.Parse(expr) is null) { MuxConsole.WriteWarning($"[daemon] Invalid cron expression: {expr}"); return; }

        var trig = new DaemonTrigger { Type = "cron", Schedule = expr, Mode = mode, Goal = goal };
        if (mode == "agent") trig.Agent = null;
        Commit(trig, $"cron {expr} -> {mode}");
    }

    // /daemon watch <glob> <mode> <goal...>
    private static void AddWatch(string rest)
    {
        var parts = rest.Split(' ', 3, System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) { MuxConsole.WriteWarning("Usage: /daemon watch <path-glob> <agent|swarm|pswarm> <goal>"); return; }
        var trig = new DaemonTrigger
        {
            Type = "watch", Path = parts[0].Trim(), Mode = NormalizeMode(parts[1]), Goal = parts[2].Trim(),
        };
        Commit(trig, $"watch {trig.Path} -> {trig.Mode}");
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

    private static string NormalizeMode(string m)
    {
        var v = (m ?? "").Trim().ToLowerInvariant();
        return v is "swarm" or "pswarm" or "agent" ? v : "agent";
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
        "  /daemon cron \"<expr>\" <mode> <goal>    add a cron trigger (mode: agent|swarm|pswarm)",
        "  /daemon watch <glob> <mode> <goal>     add a file-watch trigger",
        "  /daemon cancel <id>                    cancel a runtime trigger",
    };
}
