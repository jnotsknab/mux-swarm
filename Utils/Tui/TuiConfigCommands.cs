namespace MuxSwarm.Utils.Tui;

using System;
using System.Collections.Generic;
using System.Linq;
using MuxSwarm.Utils;

/// <summary>
/// REPL configuration commands: <c>/set &lt;key&gt; &lt;value&gt;</c> mutates a setting AND
/// persists it to Config.json; <c>/config</c> prints every setting; <c>/newagent</c> runs a
/// guided agent-scaffolding wizard. /set and /config are driven from ONE shared key registry
/// (<see cref="Keys"/>) so they can never drift - every key /config shows is settable by /set.
///
/// Each registry entry knows how to GET its current value (for /config) and SET it from a string
/// (for /set, with validation + optional live apply). A bare <c>/set</c> with no args opens an
/// interactive picker (scrollable key list -&gt; value prompt), command-preview style.
/// </summary>
internal static class TuiConfigCommands
{
    /// <summary>Result of handling a config command.</summary>
    public readonly record struct Result(bool Handled, bool Ok, string Message);

    /// <summary>One editable configuration key: how to read it, how to set it, and a value hint.</summary>
    private sealed class Key
    {
        public required string Name { get; init; }                 // canonical dotted key (matches /config)
        public string[] Aliases { get; init; } = System.Array.Empty<string>();
        public required System.Func<string> Get { get; init; }     // current value as string
        public required System.Func<string, Result> Set { get; init; } // validate + apply + persist
        public required string ValueHint { get; init; }            // e.g. "auto|tui|classic" or "<int>"

        public bool Matches(string k)
            => Name.Equals(k, System.StringComparison.OrdinalIgnoreCase)
               || Aliases.Any(a => a.Equals(k, System.StringComparison.OrdinalIgnoreCase));
    }

    // ---- shared key registry (single source of truth for /set + /config) ---------------

    private static AppConfig Cfg => App.Config;

    private static Result Ok(string msg) => new(true, true, msg);
    private static Result Bad(string msg) => new(true, false, msg);

    private static Result Save(string msg)
    {
        Common.SaveConfig(Cfg);
        return Ok(msg);
    }

    // swarm.json scalar edit helper: load, mutate, persist in one call (for /set swarm.* keys).
    private static Result SaveSwarmScalar(System.Action<SwarmConfig> mutate, string msg)
    {
        try
        {
            var swarm = LoadSwarmOrNew();
            mutate(swarm);
            SaveSwarm(swarm);
            return Ok(msg + " (run /refresh to apply).");
        }
        catch (System.Exception ex) { return Bad($"Failed to update Swarm.json: {ex.Message}"); }
    }

    private static bool TryBool(string v, out bool result)
    {
        v = (v ?? "").Trim().ToLowerInvariant();
        if (v is "true" or "on" or "1" or "yes") { result = true; return true; }
        if (v is "false" or "off" or "0" or "no") { result = false; return true; }
        result = false; return false;
    }

    private static bool TryCapMode(string v, out string mode)
    {
        v = (v ?? "").Trim().ToLowerInvariant();
        if (v is "off" or "warn" or "force") { mode = v; return true; }
        mode = "off"; return false;
    }

    private static readonly Key[] Keys =
    {
        new Key
        {
            Name = "renderMode", Aliases = new[] { "render" }, ValueHint = "auto|tui|classic",
            Get = () => Cfg.Console.RenderMode,
            Set = v =>
            {
                v = v.ToLowerInvariant();
                if (v is not ("auto" or "tui" or "classic")) return Bad($"renderMode expects auto|tui|classic (got '{v}').");
                Cfg.Console.RenderMode = v;
                return Save($"renderMode = {v}. Saved (applies on next launch).");
            },
        },
        new Key
        {
            Name = "toolOutput", Aliases = new[] { "verbose", "tool" }, ValueHint = "compact|full",
            Get = () => Cfg.Console.ToolOutput,
            Set = v =>
            {
                v = v.ToLowerInvariant();
                bool compact;
                if (v is "compact" or "collapsed" or "on") compact = true;
                else if (v is "full" or "expanded" or "off") compact = false;
                else return Bad($"toolOutput expects compact|full (got '{v}').");
                Cfg.Console.ToolOutput = compact ? "compact" : "full";
                MuxConsole.ToolOutputCompact = compact;          // live
                return Save($"toolOutput = {(compact ? "compact" : "full")}. Saved + applied live.");
            },
        },
        new Key
        {
            Name = "collapseToolLines", Aliases = new[] { "collapse", "collapse_threshold" }, ValueHint = "<int>=0",
            Get = () => Cfg.Console.CollapseToolLines.ToString(),
            Set = v =>
            {
                if (!int.TryParse(v, out int n) || n < 0) return Bad($"collapseToolLines expects a non-negative integer (got '{v}').");
                Cfg.Console.CollapseToolLines = n;
                MuxConsole.SetTuiCollapseThreshold(n);           // live
                return Save($"collapseToolLines = {n}{(n == 0 ? " (0 = never auto-collapse)" : "")}. Saved + applied live.");
            },
        },
        new Key
        {
            Name = "delegationSpacing", Aliases = new[] { "delegspacing", "delegation_spacing" }, ValueHint = "<int>=0",
            Get = () => Cfg.Console.DelegationSpacing.ToString(),
            Set = v =>
            {
                if (!int.TryParse(v, out int n) || n < 0) return Bad($"delegationSpacing expects a non-negative integer (got '{v}').");
                Cfg.Console.DelegationSpacing = n;
                MuxConsole.SetTuiDelegationSpacing(n);           // live
                return Save($"delegationSpacing = {n}{(n == 0 ? " (tight)" : " blank line(s) below each tool/delegation block")}. Saved + applied live.");
            },
        },
        new Key
        {
            Name = "brainMdCharLimit", Aliases = new[] { "brainlimit", "brain_limit" }, ValueHint = "<int>=0",
            Get = () => Cfg.ContextLimits.BrainMdCharLimit.ToString(),
            Set = v =>
            {
                if (!int.TryParse(v, out int n) || n < 0) return Bad($"brainMdCharLimit expects a non-negative integer (got '{v}').");
                Cfg.ContextLimits.BrainMdCharLimit = n;
                return Save($"brainMdCharLimit = {n}{(n == 0 ? " (off)" : " chars")}. Saved.");
            },
        },
        new Key
        {
            Name = "brainMdCapMode", Aliases = new[] { "brainmode", "brain_cap_mode" }, ValueHint = "off|warn|force",
            Get = () => Cfg.ContextLimits.BrainMdCapMode,
            Set = v =>
            {
                if (!TryCapMode(v, out string m)) return Bad($"brainMdCapMode expects off|warn|force (got '{v}').");
                Cfg.ContextLimits.BrainMdCapMode = m;
                return Save($"brainMdCapMode = {m}. Saved.");
            },
        },
        new Key
        {
            Name = "memoryMdCharLimit", Aliases = new[] { "memorylimit", "memory_limit" }, ValueHint = "<int>=0",
            Get = () => Cfg.ContextLimits.MemoryMdCharLimit.ToString(),
            Set = v =>
            {
                if (!int.TryParse(v, out int n) || n < 0) return Bad($"memoryMdCharLimit expects a non-negative integer (got '{v}').");
                Cfg.ContextLimits.MemoryMdCharLimit = n;
                return Save($"memoryMdCharLimit = {n}{(n == 0 ? " (off)" : " chars")}. Saved.");
            },
        },
        new Key
        {
            Name = "memoryMdCapMode", Aliases = new[] { "memorymode", "memory_cap_mode" }, ValueHint = "off|warn|force",
            Get = () => Cfg.ContextLimits.MemoryMdCapMode,
            Set = v =>
            {
                if (!TryCapMode(v, out string m)) return Bad($"memoryMdCapMode expects off|warn|force (got '{v}').");
                Cfg.ContextLimits.MemoryMdCapMode = m;
                return Save($"memoryMdCapMode = {m}. Saved.");
            },
        },
        new Key
        {
            Name = "swarm.maxOrchestratorIterations", Aliases = new[] { "swarm.orchiter", "maxorchiter" }, ValueHint = "<int>=1",
            Get = () => LoadSwarmOrNew().ExecutionLimits.MaxOrchestratorIterations.ToString(),
            Set = v =>
            {
                if (!int.TryParse(v, out int n) || n < 1) return Bad($"swarm.maxOrchestratorIterations expects a positive integer (got '{v}').");
                return SaveSwarmScalar(s => s.ExecutionLimits.MaxOrchestratorIterations = n, $"swarm.maxOrchestratorIterations = {n}");
            },
        },
        new Key
        {
            Name = "swarm.maxSubAgentIterations", Aliases = new[] { "swarm.subiter", "maxsubiter" }, ValueHint = "<int>=1",
            Get = () => LoadSwarmOrNew().ExecutionLimits.MaxSubAgentIterations.ToString(),
            Set = v =>
            {
                if (!int.TryParse(v, out int n) || n < 1) return Bad($"swarm.maxSubAgentIterations expects a positive integer (got '{v}').");
                return SaveSwarmScalar(s => s.ExecutionLimits.MaxSubAgentIterations = n, $"swarm.maxSubAgentIterations = {n}");
            },
        },
        new Key
        {
            Name = "swarm.maxSubTaskRetries", Aliases = new[] { "swarm.retries", "maxsubretries" }, ValueHint = "<int>=0",
            Get = () => LoadSwarmOrNew().ExecutionLimits.MaxSubTaskRetries.ToString(),
            Set = v =>
            {
                if (!int.TryParse(v, out int n) || n < 0) return Bad($"swarm.maxSubTaskRetries expects a non-negative integer (got '{v}').");
                return SaveSwarmScalar(s => s.ExecutionLimits.MaxSubTaskRetries = n, $"swarm.maxSubTaskRetries = {n}");
            },
        },
        new Key
        {
            Name = "swarm.maxToolIterationsPerTurn", Aliases = new[] { "swarm.tooliter", "maxtooliter" }, ValueHint = "<int> (<=0 = unlimited)",
            Get = () => LoadSwarmOrNew().ExecutionLimits.MaxToolIterationsPerTurn.ToString(),
            Set = v =>
            {
                if (!int.TryParse(v, out int n)) return Bad($"swarm.maxToolIterationsPerTurn expects an integer (got '{v}').");
                return SaveSwarmScalar(s => s.ExecutionLimits.MaxToolIterationsPerTurn = n, $"swarm.maxToolIterationsPerTurn = {n}");
            },
        },
        new Key
        {
            Name = "swarm.maxAutoContinuesPerTurn", Aliases = new[] { "swarm.autocontinue", "maxautocontinue" }, ValueHint = "<int>=0",
            Get = () => LoadSwarmOrNew().ExecutionLimits.MaxAutoContinuesPerTurn.ToString(),
            Set = v =>
            {
                if (!int.TryParse(v, out int n) || n < 0) return Bad($"swarm.maxAutoContinuesPerTurn expects a non-negative integer (got '{v}').");
                return SaveSwarmScalar(s => s.ExecutionLimits.MaxAutoContinuesPerTurn = n, $"swarm.maxAutoContinuesPerTurn = {n}");
            },
        },
        new Key
        {
            Name = "dockedFooter", Aliases = new[] { "footer" }, ValueHint = "on|off",
            Get = () => Cfg.Console.DockedFooter.ToString(),
            Set = v =>
            {
                if (!TryBool(v, out bool on)) return Bad($"dockedFooter expects a boolean (got '{v}').");
                Cfg.Console.DockedFooter = on;
                return Save($"dockedFooter = {on}. Saved (applies on next launch).");
            },
        },
        new Key
        {
            Name = "collapseSubAgents", Aliases = new[] { "subagentview", "sav", "collapsesub" }, ValueHint = "on|off",
            Get = () => Cfg.Console.CollapseSubAgents.ToString(),
            Set = v =>
            {
                if (!TryBool(v, out bool on)) return Bad($"collapseSubAgents expects a boolean (got '{v}').");
                Cfg.Console.CollapseSubAgents = on;
                MuxConsole.CollapseSubAgents = on;               // live
                return Save($"collapseSubAgents = {on}. Saved + applied live.");
            },
        },
        new Key
        {
            Name = "collapseDaemon", Aliases = new[] { "daemonview", "dv", "collapsedaemon" }, ValueHint = "on|off",
            Get = () => Cfg.Console.CollapseDaemon.ToString(),
            Set = v =>
            {
                if (!TryBool(v, out bool on)) return Bad($"collapseDaemon expects a boolean (got '{v}').");
                Cfg.Console.CollapseDaemon = on;
                MuxConsole.CollapseDaemonOutput = on;            // live
                return Save($"collapseDaemon = {on}. Saved + applied live.");
            },
        },
        new Key
        {
            Name = "inputHighlight", Aliases = new[] { "inputshade", "inputbg" }, ValueHint = "on|off",
            Get = () => Cfg.Console.InputHighlight.ToString(),
            Set = v =>
            {
                if (!TryBool(v, out bool on)) return Bad($"inputHighlight expects a boolean (got '{v}').");
                Cfg.Console.InputHighlight = on;
                MuxConsole.SetTuiInputHighlight(on);             // live
                return Save($"inputHighlight = {on}. Saved + applied live.");
            },
        },
        new Key
        {
            Name = "cardMarkdown", Aliases = new[] { "cardmd", "panelmarkdown" }, ValueHint = "on|off",
            Get = () => Cfg.Console.CardMarkdown.ToString(),
            Set = v =>
            {
                if (!TryBool(v, out bool on)) return Bad($"cardMarkdown expects a boolean (got '{v}').");
                Cfg.Console.CardMarkdown = on;
                MuxConsole.SetTuiCardMarkdown(on);               // live
                return Save($"cardMarkdown = {on}. Saved + applied live.");
            },
        },
        new Key
        {
            Name = "collapseDelegations", Aliases = new[] { "collapsedeleg", "delegcollapse" }, ValueHint = "on|off",
            Get = () => Cfg.Console.CollapseDelegations.ToString(),
            Set = v =>
            {
                if (!TryBool(v, out bool on)) return Bad($"collapseDelegations expects a boolean (got '{v}').");
                Cfg.Console.CollapseDelegations = on;
                MuxConsole.CollapseDelegations = on;             // live
                return Save($"collapseDelegations = {on}. Saved + applied live.");
            },
        },
        new Key
        {
            Name = "bracketedPaste", Aliases = new[] { "paste", "multilinepaste" }, ValueHint = "on|off",
            Get = () => Cfg.Console.BracketedPaste.ToString(),
            Set = v =>
            {
                if (!TryBool(v, out bool on)) return Bad($"bracketedPaste expects a boolean (got '{v}').");
                Cfg.Console.BracketedPaste = on;
                MuxConsole.SetTuiBracketedPaste(on);             // live
                return Save($"bracketedPaste = {on}. Saved + applied live.");
            },
        },
        new Key
        {
            Name = "ultra.thinkingBudget", Aliases = new[] { "thinkingbudget", "ultra.budget" }, ValueHint = "<int>",
            Get = () => Cfg.Ultra.ThinkingBudget.ToString(),
            Set = v =>
            {
                if (!int.TryParse(v, out int n) || n < 0) return Bad($"ultra.thinkingBudget expects a non-negative integer (got '{v}').");
                Cfg.Ultra.ThinkingBudget = n;
                return Save($"ultra.thinkingBudget = {n}. Saved (applies to the next /ultra session).");
            },
        },
        new Key
        {
            Name = "ultra.includeSubAgents", Aliases = new[] { "ultra.subagents" }, ValueHint = "on|off",
            Get = () => Cfg.Ultra.IncludeSubAgents.ToString(),
            Set = v =>
            {
                if (!TryBool(v, out bool on)) return Bad($"ultra.includeSubAgents expects a boolean (got '{v}').");
                Cfg.Ultra.IncludeSubAgents = on;
                return Save($"ultra.includeSubAgents = {on}. Saved.");
            },
        },
        new Key
        {
            Name = "ultra.autoSubAgents", Aliases = new[] { "ultra.auto" }, ValueHint = "on|off",
            Get = () => Cfg.Ultra.AutoSubAgents.ToString(),
            Set = v =>
            {
                if (!TryBool(v, out bool on)) return Bad($"ultra.autoSubAgents expects a boolean (got '{v}').");
                Cfg.Ultra.AutoSubAgents = on;
                return Save($"ultra.autoSubAgents = {on}. Saved.");
            },
        },
        new Key
        {
            Name = "isUsingDockerForExec", Aliases = new[] { "dockerexec", "docker" }, ValueHint = "on|off",
            Get = () => Cfg.IsUsingDockerForExec.ToString(),
            Set = v =>
            {
                if (!TryBool(v, out bool on)) return Bad($"isUsingDockerForExec expects a boolean (got '{v}').");
                Cfg.IsUsingDockerForExec = on;
                return Save($"isUsingDockerForExec = {on}. Saved.");
            },
        },
        new Key
        {
            Name = "serveAddress", Aliases = new[] { "serve.address" }, ValueHint = "<host>",
            Get = () => Cfg.ServeAddress,
            Set = v =>
            {
                v = v.Trim();
                if (v.Length == 0) return Bad("serveAddress cannot be empty.");
                Cfg.ServeAddress = v;
                return Save($"serveAddress = {v}. Saved (applies on next --serve).");
            },
        },
        new Key
        {
            Name = "serve.editable", ValueHint = "on|off",
            Get = () => Cfg.Serve.Editable.ToString(),
            Set = v =>
            {
                if (!TryBool(v, out bool on)) return Bad($"serve.editable expects a boolean (got '{v}').");
                Cfg.Serve.Editable = on;
                return Save($"serve.editable = {on}. Saved (applies on next --serve).");
            },
        },
        new Key
        {
            Name = "serve.auth.enabled", Aliases = new[] { "serve.auth" }, ValueHint = "on|off",
            Get = () => Cfg.Serve.Auth.Enabled.ToString(),
            Set = v =>
            {
                if (!TryBool(v, out bool on)) return Bad($"serve.auth.enabled expects a boolean (got '{v}').");
                Cfg.Serve.Auth.Enabled = on;
                return Save($"serve.auth.enabled = {on}. Saved (applies on next --serve).");
            },
        },
        new Key
        {
            Name = "showReasoning", Aliases = new[] { "showreasoning", "reasoning" }, ValueHint = "full|summary|none",
            Get = () => Cfg.ShowReasoning,
            Set = v =>
            {
                var n = (v ?? "").Trim().ToLowerInvariant();
                string canon =
                    n is "none" or "off" or "hide" ? "none" :
                    n is "summary" or "summarized" ? "summary" :
                    n is "full" or "all" or "on" or "show" ? "full" : "";
                if (canon.Length == 0) return Bad($"showReasoning expects full|summary|none (got '{v}').");
                Cfg.ShowReasoning = canon;
                MuxConsole.ShowReasoning = canon;
                return Save($"showReasoning = {canon}. Saved ({(canon == "none" ? "streamed reasoning hidden" : "streamed reasoning shown")}).");
            },
        },
    };

    private static Key? Find(string key) => Keys.FirstOrDefault(k => k.Matches(key));

    // ---- reflection-driven coverage: every scalar leaf of config.json + swarm.json ----------
    // The hand-written Keys[] above keep priority (they carry live side-effects like instant
    // repaint). Everything ELSE in the two config object graphs is exposed automatically by
    // walking the typed models, so /set + /config cover every possible arg without hand-listing.

    private static void PersistAppConfig() => Common.SaveConfig(Cfg);

    private static void PersistSwarm()
    {
        var swarm = App.SwarmConfig;
        if (swarm is null) return;
        System.IO.File.WriteAllText(PlatformContext.SwarmPath,
            System.Text.Json.JsonSerializer.Serialize(swarm, SwarmJsonOpts));
    }

    /// <summary>Build a Key for one reflected leaf, routing persistence to the right file.</summary>
    private static Key ReflectedKey(ConfigReflector.Leaf leaf, bool swarm)
    {
        return new Key
        {
            Name = leaf.Path,
            ValueHint = leaf.TypeHint,
            Get = leaf.Get,
            Set = v =>
            {
                var (ok, msg) = leaf.Set(v);
                if (!ok) return Bad(msg);
                if (swarm) PersistSwarm(); else PersistAppConfig();
                return Ok(msg + (swarm
                    ? ". Saved to swarm.json (run /refresh to apply)."
                    : ". Saved to config.json."));
            },
        };
    }

    /// <summary>All settable keys: explicit (priority, with live apply) + reflected leaves for
    /// every other scalar in config.json and swarm.json. Reflected keys whose dotted path is
    /// already owned by an explicit key are skipped so the explicit (live-apply) version wins.</summary>
    private static List<Key> AllKeys()
    {
        var all = new List<Key>(Keys);
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in Keys)
        {
            owned.Add(k.Name);
            foreach (var a in k.Aliases) owned.Add(a);
        }
        foreach (var leaf in ConfigReflector.Walk(Cfg, ""))
            if (owned.Add(leaf.Path)) all.Add(ReflectedKey(leaf, swarm: false));
        if (App.SwarmConfig is not null)
            foreach (var leaf in ConfigReflector.Walk(App.SwarmConfig, "swarm"))
                if (owned.Add(leaf.Path)) all.Add(ReflectedKey(leaf, swarm: true));
        return all;
    }

    private static Key? FindAny(string key) => AllKeys().FirstOrDefault(k => k.Matches(key));

    // ---- command surface ---------------------------------------------------------------

    /// <summary>True when <paramref name="cmd"/> is a config command this handler owns.</summary>
    public static bool IsConfigCommand(string cmd)
    {
        var c = (cmd ?? "").Trim().Split(' ', 2, System.StringSplitOptions.RemoveEmptyEntries);
        if (c.Length == 0) return false;
        var head = c[0].ToLowerInvariant();
        return head is "/set" or "/config" or "/newagent";
    }

    /// <summary>
    /// Handle /set or /config (non-interactive form). Returns a Result with a user-facing message;
    /// the caller decides how to print it (works in TUI + classic). The interactive bare-/set
    /// picker and the /newagent wizard are run via <see cref="RunInteractive"/> because they block
    /// on prompts.
    /// </summary>
    public static Result Handle(string raw)
    {
        var input = (raw ?? "").Trim();
        var parts = input.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        var head = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";

        if (head == "/config")
            return Ok(ShowConfig());

        if (head == "/newagent")
            return CreateAgentQuick(parts);   // quick path; rich wizard is RunInteractive

        if (head is "/editagent" or "/delagent" or "/removeagent")
            return Bad($"{head} is interactive; just run it with no value to open the picker.");

        if (head == "/createteam")
            return Bad("/createteam is interactive; just run it to open the guided wizard.");

        if (head != "/set")
            return new Result(false, false, "");

        if (parts.Length < 3)
            return Bad("Usage: /set <key> <value>   (run /set with no args for an interactive picker, or /config to list keys)");

        var key = parts[1];
        var value = string.Join(' ', parts[2..]).Trim();
        var entry = FindAny(key);
        if (entry is null)
            return Bad($"Unknown setting '{key}'. Run /config to see all keys, or /set for an interactive picker.");

        try { return entry.Set(value); }
        catch (System.Exception ex) { return Bad($"Failed to apply /set {key}: {ex.Message}"); }
    }

    /// <summary>
    /// True when the command needs the interactive (blocking-prompt) path: a bare <c>/set</c>
    /// (key picker) or any <c>/newagent</c> (guided wizard). The caller runs <see cref="RunInteractive"/>.
    /// </summary>
    public static bool NeedsInteractive(string raw)
    {
        var parts = (raw ?? "").Trim().Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        var head = parts[0].ToLowerInvariant();
        if (head == "/set" && parts.Length < 3) return true;     // bare /set or "/set key" -> picker
        if (head == "/newagent") return true;                    // always wizard (uses prompts)
        if (head is "/editagent" or "/delagent" or "/removeagent") return true;
        if (head == "/createteam") return true;                  // always wizard (uses prompts)
        return false;
    }

    /// <summary>
    /// Run the interactive flows that block on prompts: the bare-<c>/set</c> key picker and the
    /// <c>/newagent</c> wizard. Returns a final Result to print. <paramref name="spawnHelper"/> is
    /// invoked when the user opts to have an agent draft the new agent's prompt (mirrors /onboard);
    /// it receives the new agent name + description and should run a scaffolding session.
    /// </summary>
    public static Result RunInteractive(string raw, System.Action<string, string>? spawnHelper = null)
    {
        var parts = (raw ?? "").Trim().Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        var head = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";

        if (head == "/set")
            return RunSetPicker(parts);

        if (head == "/newagent")
            return RunNewAgentWizard(parts, spawnHelper);

        if (head == "/editagent")
            return RunEditAgentWizard(parts);

        if (head is "/delagent" or "/removeagent")
            return RunDeleteAgentWizard(parts);

        if (head == "/createteam")
            return RunCreateTeamWizard(parts);

        return new Result(false, false, "");
    }

    // ---- /set interactive picker -------------------------------------------------------

    private static Result RunSetPicker(string[] parts)
    {
        // If a key was given (but no value), jump straight to its value prompt; otherwise show the
        // scrollable key list first.
        var keys = AllKeys();
        Key? entry = parts.Length >= 2 ? FindAny(parts[1]) : null;
        if (entry is null)
        {
            var labels = keys.Select(k => $"{k.Name}  =  {k.Get()}   [{k.ValueHint}]").ToList();
            labels.Add("(cancel)");
            var chosen = MuxConsole.Select("Select a setting to change", labels);
            if (chosen.StartsWith("(cancel)")) return Ok("No changes made.");
            int idx = labels.IndexOf(chosen);
            if (idx < 0 || idx >= keys.Count) return Ok("No changes made.");
            entry = keys[idx];
        }

        var value = MuxConsole.Prompt($"New value for {entry.Name} [{entry.ValueHint}] (current: {entry.Get()})");
        if (string.IsNullOrWhiteSpace(value)) return Ok("No changes made.");

        try { return entry.Set(value.Trim()); }
        catch (System.Exception ex) { return Bad($"Failed to apply {entry.Name}: {ex.Message}"); }
    }

    // ---- /newagent: quick path + guided wizard -----------------------------------------

    private static (bool ok, string err) ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || name.Contains(' '))
            return (false, $"Invalid agent name '{name}'. Use a single word, file-system safe (no spaces).");
        return (true, "");
    }

    private static System.Text.Json.JsonSerializerOptions SwarmJsonOpts => new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static SwarmConfig LoadSwarmOrNew()
    {
        var path = PlatformContext.SwarmPath;
        if (System.IO.File.Exists(path))
            return System.Text.Json.JsonSerializer.Deserialize<SwarmConfig>(System.IO.File.ReadAllText(path), SwarmJsonOpts) ?? new SwarmConfig();
        return new SwarmConfig();
    }

    /// <summary>Non-interactive /newagent (e.g. "/newagent name desc") used when the caller does
    /// not run the wizard. Creates a minimal agent (no MCP servers) + starter prompt.</summary>
    private static Result CreateAgentQuick(string[] parts)
    {
        if (parts.Length < 2)
            return Bad("Usage: /newagent <name> [description]   (run /newagent for the guided wizard)");
        string name = parts[1].Trim();
        var (ok, err) = ValidateName(name);
        if (!ok) return Bad(err);
        string description = parts.Length > 2 ? string.Join(' ', parts[2..]).Trim() : $"{name} agent";
        return WriteAgent(name, description, new List<string>(), model: null, canDelegate: false, spawnHelper: null, useHelper: false);
    }

    /// <summary>Guided /newagent wizard: name -> description -> MCP servers -> model -> canDelegate
    /// -> prompt authoring (helper agent or starter template). Uses the blocking MuxConsole prompts
    /// (TUI-aware), mirroring the /onboard flow.</summary>
    private static Result RunNewAgentWizard(string[] parts, System.Action<string, string>? spawnHelper)
    {
        // Name (from args or prompt).
        string name = parts.Length >= 2 ? parts[1].Trim() : MuxConsole.Prompt("New agent name (single word)").Trim();
        var (ok, err) = ValidateName(name);
        if (!ok) return Bad(err);

        var swarm = LoadSwarmOrNew();
        if (swarm.Agents.Exists(a => string.Equals(a.Name, name, System.StringComparison.OrdinalIgnoreCase)))
            return Bad($"An agent named '{name}' already exists in Swarm.json. Pick another name or edit it directly.");

        // Description.
        string descDefault = parts.Length > 2 ? string.Join(' ', parts[2..]).Trim() : $"{name} agent";
        string description = MuxConsole.Prompt("One-line description of what this agent does", descDefault).Trim();
        if (string.IsNullOrWhiteSpace(description)) description = descDefault;

        // MCP servers (multi-select from the configured servers in Config.json).
        var available = (Cfg.McpServers?.Keys ?? Enumerable.Empty<string>())
            .OrderBy(s => s, System.StringComparer.OrdinalIgnoreCase).ToList();
        var chosenServers = new List<string>();
        if (available.Count > 0)
        {
            if (MuxConsole.Confirm($"Grant MCP tool servers to '{name}'? ({available.Count} available)", true))
                chosenServers = MuxConsole.MultiSelect("Select MCP servers (space to toggle, enter to confirm)", available);
        }
        else
        {
            MuxConsole.WriteMuted("No MCP servers configured in Config.json; the agent will start with no MCP tools.");
        }

        // Model (default = swarm default, or pick one used elsewhere in the swarm).
        string? model = null;
        var knownModels = swarm.Agents.Where(a => !string.IsNullOrWhiteSpace(a.Model)).Select(a => a.Model!)
            .Concat(new[] { swarm.SingleAgent?.Model, swarm.Orchestrator?.Model }.Where(m => !string.IsNullOrWhiteSpace(m))!)
            .Distinct(System.StringComparer.OrdinalIgnoreCase).ToList();
        var modelChoices = new List<string> { "(use swarm default)" };
        modelChoices.AddRange(knownModels!);
        modelChoices.Add("(enter a model id)");
        var modelPick = MuxConsole.Select("Model for this agent", modelChoices);
        if (modelPick == "(enter a model id)")
        {
            var m = MuxConsole.Prompt("Model id (e.g. claude-sonnet-4-6)").Trim();
            model = string.IsNullOrWhiteSpace(m) ? null : m;
        }
        else if (modelPick != "(use swarm default)")
        {
            model = modelPick;
        }

        // Delegation.
        bool canDelegate = MuxConsole.Confirm($"Allow '{name}' to delegate to other agents?", false);

        // Prompt authoring: helper agent vs starter template.
        bool useHelper = false;
        if (spawnHelper is not null)
        {
            var promptChoice = MuxConsole.Select("How should the agent's prompt be written?", new[]
            {
                "Spawn a helper agent to draft it (interactive, like /onboard)",
                "Drop a starter template I can edit later",
            });
            useHelper = promptChoice.StartsWith("Spawn");
        }

        return WriteAgent(name, description, chosenServers, model, canDelegate, spawnHelper, useHelper);
    }

    /// <summary>Persist a new agent to Swarm.json + write/seed its prompt file. When
    /// <paramref name="useHelper"/> is true and a spawn callback is provided, the prompt file is
    /// seeded with a brief stub and the helper is invoked to flesh it out interactively.</summary>
    private static Result WriteAgent(string name, string description, List<string> mcpServers,
        string? model, bool canDelegate, System.Action<string, string>? spawnHelper, bool useHelper)
    {
        try
        {
            var swarm = LoadSwarmOrNew();
            if (swarm.Agents.Exists(a => string.Equals(a.Name, name, System.StringComparison.OrdinalIgnoreCase)))
                return Bad($"An agent named '{name}' already exists in Swarm.json.");

            string promptRel = $"{name}.md";
            string promptDir = PlatformContext.PromptsDirectory;
            System.IO.Directory.CreateDirectory(promptDir);
            string promptAbs = System.IO.Path.Combine(promptDir, promptRel);
            if (!System.IO.File.Exists(promptAbs))
            {
                string starter = string.Join("\n", new[]
                {
                    $"# {name}", "",
                    $"You are **{name}**. {description}", "",
                    "## Responsibilities", "- Describe what this agent is responsible for.", "",
                    "## Approach", "- Tone, rigor, when to delegate or ask.", "",
                    "## Constraints", "- Any guardrails or boundaries.", "",
                });
                System.IO.File.WriteAllText(promptAbs, starter);
            }

            swarm.Agents.Add(new AgentConfig
            {
                Name = name,
                Description = description,
                PromptPath = promptRel,
                CanDelegate = canDelegate,
                Model = model,
                McpServers = mcpServers ?? new List<string>(),
            });
            System.IO.File.WriteAllText(PlatformContext.SwarmPath, System.Text.Json.JsonSerializer.Serialize(swarm, SwarmJsonOpts));

            string serversNote = (mcpServers is { Count: > 0 })
                ? $"MCP: {string.Join(", ", mcpServers)}"
                : "MCP: none";
            string modelNote = model is null ? "model: swarm default" : $"model: {model}";

            // Optionally hand off to a helper agent to author the prompt interactively.
            if (useHelper && spawnHelper is not null)
            {
                MuxConsole.WriteInfo($"Spawning a helper to draft the prompt for '{name}'...");
                try { spawnHelper(name, description); }
                catch (System.Exception ex) { MuxConsole.WriteWarning($"Helper session failed ({ex.Message}); starter template kept."); }
            }

            return Ok(
                $"Created agent '{name}'.\n" +
                $"  Swarm.json entry added  ({serversNote}; {modelNote}; canDelegate={canDelegate})\n" +
                $"  Prompt: {promptAbs}\n" +
                $"Run /refresh to load it, then launch with /swap or --agent {name}.");
        }
        catch (System.Exception ex)
        {
            return Bad($"Failed to create agent: {ex.Message}");
        }
    }

    // ---- /createteam: guided team-creation wizard --------------------------------------

    private static (bool ok, string err) ValidateTeamName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || name.Contains(' '))
            return (false, $"Invalid team name '{name}'. Use a single word, file-system safe (no spaces).");
        return (true, "");
    }

    /// <summary>Guided /createteam wizard: name -> description -> lead -> members -> coordination
    /// -> maxParallel. Persists an additive entry to swarm.json teams[] (mirrors /newagent). The
    /// lead + members are picked from the agents already defined in swarm.json.</summary>
    private static Result RunCreateTeamWizard(string[] parts)
    {
        var swarm = LoadSwarmOrNew();

        // Roster of selectable agents (named agents + Orchestrator as a possible lead).
        var agentNames = swarm.Agents.Select(a => a.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(System.StringComparer.OrdinalIgnoreCase).ToList();
        if (agentNames.Count == 0)
            return Bad("No agents defined in swarm.json yet. Use /newagent first, then /createteam.");

        // Name (from args or prompt).
        string name = parts.Length >= 2 ? parts[1].Trim() : MuxConsole.Prompt("New team name (single word)").Trim();
        var (ok, err) = ValidateTeamName(name);
        if (!ok) return Bad(err);
        if (swarm.Teams.Exists(t => string.Equals(t.Name, name, System.StringComparison.OrdinalIgnoreCase)))
            return Bad($"A team named '{name}' already exists in swarm.json. Pick another name or edit it directly.");

        // Description.
        string descDefault = parts.Length > 2 ? string.Join(' ', parts[2..]).Trim() : $"{name} team";
        string description = MuxConsole.Prompt("One-line description of what this team does", descDefault).Trim();
        if (string.IsNullOrWhiteSpace(description)) description = descDefault;

        // Lead: an existing agent, or the Orchestrator.
        var leadChoices = new List<string> { "Orchestrator" };
        leadChoices.AddRange(agentNames.Where(n => !n.Equals("Orchestrator", System.StringComparison.OrdinalIgnoreCase)));
        string lead = MuxConsole.Select("Lead (coordinator) for this team", leadChoices);

        // Members: multi-select from the agents (excluding the lead).
        var memberPool = agentNames
            .Where(n => !n.Equals(lead, System.StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase).ToList();
        var members = new List<string>();
        if (memberPool.Count > 0)
            members = MuxConsole.MultiSelect("Select team members (space to toggle, enter to confirm)", memberPool);
        else
            MuxConsole.WriteMuted("No other agents available as members; the team will run with just the lead.");

        // Coordination policy.
        string coordination = MuxConsole.Select("Coordination policy", new[]
        {
            "fanout    (independent concurrent tasks)",
            "taskboard (shared task graph: deps + claiming + Ctrl+T board)",
        }).StartsWith("taskboard") ? "taskboard" : "fanout";

        // Max parallel members (optional; blank = inherit /maxp).
        int? maxParallel = null;
        var mp = MuxConsole.Prompt("Max members running in parallel (blank = use /maxp default)", "").Trim();
        if (!string.IsNullOrWhiteSpace(mp) && int.TryParse(mp, out var mpVal) && mpVal > 0)
            maxParallel = mpVal;

        return WriteTeam(name, description, lead, members, coordination, maxParallel);
    }

    /// <summary>Persist a new team to swarm.json teams[] (additive). User runs /refresh, then
    /// launches it with /teams &lt;name&gt;.</summary>
    private static Result WriteTeam(string name, string description, string lead,
        List<string> members, string coordination, int? maxParallel)
    {
        try
        {
            var swarm = LoadSwarmOrNew();
            if (swarm.Teams.Exists(t => string.Equals(t.Name, name, System.StringComparison.OrdinalIgnoreCase)))
                return Bad($"A team named '{name}' already exists in swarm.json.");

            swarm.Teams.Add(new TeamConfig
            {
                Name = name,
                Description = description,
                Lead = lead,
                Members = members ?? new List<string>(),
                Coordination = coordination,
                MaxParallel = maxParallel,
            });
            SaveSwarm(swarm);

            string membersNote = (members is { Count: > 0 }) ? string.Join(", ", members) : "(none)";
            string mpNote = maxParallel is { } v ? $"maxParallel: {v}" : "maxParallel: /maxp default";
            return Ok(
                $"Created team '{name}'.\n" +
                $"  swarm.json teams[] entry added  (lead={lead}; coordination={coordination}; {mpNote})\n" +
                $"  members: {membersNote}\n" +
                $"Run /refresh to load it, then launch with /teams {name}.");
        }
        catch (System.Exception ex)
        {
            return Bad($"Failed to create team: {ex.Message}");
        }
    }

    private static void SaveSwarm(SwarmConfig swarm) =>
        System.IO.File.WriteAllText(PlatformContext.SwarmPath, System.Text.Json.JsonSerializer.Serialize(swarm, SwarmJsonOpts));

    /// <summary>Pick an agent from swarm.Agents interactively; returns its index or -1 on cancel.</summary>
    private static int PickAgent(SwarmConfig swarm, string verb)
    {
        if (swarm.Agents.Count == 0) return -2; // sentinel: none exist
        var labels = swarm.Agents
            .Select(a => $"{a.Name}  ({a.Model ?? "swarm default"}; canDelegate={a.CanDelegate}; MCP={(a.McpServers.Count)})")
            .ToList();
        labels.Add("(cancel)");
        var chosen = MuxConsole.Select($"Select an agent to {verb}", labels);
        if (chosen.StartsWith("(cancel)")) return -1;
        int idx = labels.IndexOf(chosen);
        return (idx < 0 || idx >= swarm.Agents.Count) ? -1 : idx;
    }

    /// <summary>/editagent: pick an agent, then edit description / model / canDelegate / MCP servers.
    /// The prompt file is left to the user (path is shown). Saves swarm.json; user runs /refresh.</summary>
    private static Result RunEditAgentWizard(string[] parts)
    {
        var swarm = LoadSwarmOrNew();
        int idx;
        if (parts.Length >= 2)
        {
            idx = swarm.Agents.FindIndex(a => string.Equals(a.Name, parts[1].Trim(), System.StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return Bad($"No agent named '{parts[1].Trim()}' in Swarm.json.");
        }
        else
        {
            idx = PickAgent(swarm, "edit");
        }
        if (idx == -2) return Bad("No agents defined in Swarm.json yet. Use /newagent first.");
        if (idx < 0) return Ok("No changes made.");

        var agent = swarm.Agents[idx];
        var field = MuxConsole.Select($"Edit '{agent.Name}' - which field?", new[]
        {
            "description", "model", "canDelegate", "mcpServers", "(cancel)"
        });

        switch (field)
        {
            case "description":
            {
                var v = MuxConsole.Prompt("New description", agent.Description).Trim();
                if (string.IsNullOrWhiteSpace(v)) return Ok("No changes made.");
                agent.Description = v;
                break;
            }
            case "model":
            {
                var knownModels = swarm.Agents.Where(a => !string.IsNullOrWhiteSpace(a.Model)).Select(a => a.Model!)
                    .Concat(new[] { swarm.SingleAgent?.Model, swarm.Orchestrator?.Model }.Where(m => !string.IsNullOrWhiteSpace(m))!)
                    .Distinct(System.StringComparer.OrdinalIgnoreCase).ToList();
                var choices = new List<string> { "(use swarm default)" };
                choices.AddRange(knownModels!);
                choices.Add("(enter a model id)");
                choices.Add("(cancel)");
                var pick = MuxConsole.Select($"Model for '{agent.Name}' (current: {agent.Model ?? "swarm default"})", choices);
                if (pick.StartsWith("(cancel)")) return Ok("No changes made.");
                if (pick == "(enter a model id)")
                {
                    var m = MuxConsole.Prompt("Model id").Trim();
                    agent.Model = string.IsNullOrWhiteSpace(m) ? null : m;
                }
                else agent.Model = pick == "(use swarm default)" ? null : pick;
                break;
            }
            case "canDelegate":
            {
                agent.CanDelegate = MuxConsole.Confirm($"Allow '{agent.Name}' to delegate to other agents?", agent.CanDelegate);
                break;
            }
            case "mcpServers":
            {
                var available = (Cfg.McpServers?.Keys ?? Enumerable.Empty<string>())
                    .OrderBy(s => s, System.StringComparer.OrdinalIgnoreCase).ToList();
                if (available.Count == 0) return Bad("No MCP servers configured in Config.json.");
                var picked = MuxConsole.MultiSelect(
                    $"MCP servers for '{agent.Name}' (currently: {(agent.McpServers.Count > 0 ? string.Join(", ", agent.McpServers) : "none")})",
                    available);
                agent.McpServers = picked;
                break;
            }
            default:
                return Ok("No changes made.");
        }

        SaveSwarm(swarm);
        return Ok($"Updated agent '{agent.Name}'. Run /refresh to apply.");
    }

    /// <summary>/delagent: pick an agent, confirm, remove it from swarm.json. Optionally also delete
    /// its prompt file. Saves swarm.json; user runs /refresh.</summary>
    private static Result RunDeleteAgentWizard(string[] parts)
    {
        var swarm = LoadSwarmOrNew();
        int idx;
        if (parts.Length >= 2)
        {
            idx = swarm.Agents.FindIndex(a => string.Equals(a.Name, parts[1].Trim(), System.StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return Bad($"No agent named '{parts[1].Trim()}' in Swarm.json.");
        }
        else
        {
            idx = PickAgent(swarm, "remove");
        }
        if (idx == -2) return Bad("No agents defined in Swarm.json.");
        if (idx < 0) return Ok("No changes made.");

        var agent = swarm.Agents[idx];
        if (!MuxConsole.Confirm($"Remove agent '{agent.Name}' from Swarm.json? This cannot be undone.", false))
            return Ok("No changes made.");

        string? promptRel = agent.PromptPath;
        swarm.Agents.RemoveAt(idx);
        SaveSwarm(swarm);

        // Offer to also delete the prompt file (only if it lives under Prompts/Agents).
        if (!string.IsNullOrWhiteSpace(promptRel))
        {
            try
            {
                var promptAbs = System.IO.Path.IsPathRooted(promptRel)
                    ? promptRel
                    : System.IO.Path.Combine(PlatformContext.PromptsDirectory, promptRel);
                if (System.IO.File.Exists(promptAbs)
                    && MuxConsole.Confirm($"Also delete its prompt file ({System.IO.Path.GetFileName(promptAbs)})?", false))
                {
                    System.IO.File.Delete(promptAbs);
                }
            }
            catch (System.Exception ex) { MuxConsole.WriteWarning($"Could not delete prompt file: {ex.Message}"); }
        }

        return Ok($"Removed agent '{agent.Name}'. Run /refresh to apply.");
    }

    // ---- /config ----------------------------------------------------------------------

    private static string ShowConfig()
    {
        var keys = AllKeys();
        int keyW = keys.Max(k => k.Name.Length);
        int valW = keys.Max(k => System.Math.Min(k.Get().Length, 40));
        var sb = new System.Text.StringBuilder();
        sb.Append("Current configuration (every key is settable with /set):");
        foreach (var k in keys)
        {
            sb.Append('\n');
            sb.Append("  ").Append(k.Name.PadRight(keyW))
              .Append("  ").Append(k.Get().PadRight(valW))
              .Append("   [").Append(k.ValueHint).Append(']');
        }
        sb.Append("\n\nEdit: /set <key> <value>   |   /set (no args) for an interactive picker");
        return sb.ToString();
    }
}
