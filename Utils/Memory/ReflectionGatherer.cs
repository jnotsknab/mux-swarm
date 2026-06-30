using System.Text;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Utils.Memory;

/// <summary>
/// Background "gatherer" for deep memory. When reflectionAgent.mode == "deep" it runs a
/// fire-and-forget loop (mirrors <see cref="ContextCap.StartPulse"/>): every pollIntervalSeconds
/// it wakes, and ONLY when activity has occurred since the last reflection does it make ONE light
/// model call to distill durable reflections from the recent session, writing them to
/// <c>Context/Reflections/</c> (filesystem-first) and best-effort mirroring into Chroma/KG.
///
/// Activity-gated: no activity -> no LLM call (the cost control). Inert unless deep mode is on.
/// Best-effort throughout; never throws into the caller.
/// </summary>
public static class ReflectionGatherer
{
    private static int _started;                 // 0/1 guard so we only spawn one loop
    private static long _activityTicks;          // monotonic activity counter
    private static long _reflectedAtTicks;       // activity value at last reflection
    private static DateTimeOffset _lastReflection = DateTimeOffset.MinValue;

    /// <summary>Snapshot provider for the recent conversation - set by the active session so the
    /// gatherer can observe history without owning it. Returns a copy; null when no session.</summary>
    public static Func<IReadOnlyList<ChatMessage>>? HistoryProvider { get; set; }

    /// <summary>Record that something happened worth reflecting on (a completed turn, tool result,
    /// user input). Cheap and lock-free; the loop reads the counter on its own cadence.</summary>
    public static void Touch() => Interlocked.Increment(ref _activityTicks);

    /// <summary>True when deep mode is configured.</summary>
    public static bool IsDeep()
    {
        try { return App.SwarmConfig?.ResolveReflection().IsDeep == true; }
        catch { return false; }
    }

    /// <summary>
    /// Start the background gatherer loop if deep mode is on. No-op when already started or when
    /// mode != deep. Interactive sessions only - the caller gates out serve/acp/stdio.
    /// </summary>
    public static void Start(Func<string, IChatClient>? chatClientFactory, CancellationToken ct = default)
    {
        if (!IsDeep()) return;
        if (Interlocked.Exchange(ref _started, 1) == 1) return;

        var cfg = App.SwarmConfig!.ResolveReflection();
        int seconds = Math.Max(15, cfg.PollIntervalSeconds);

        _ = Task.Run(async () =>
        {
            try
            {
                // Settle delay so a brand-new session has something to reflect on first.
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(seconds, 45)), ct);
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        // Re-read config each tick so a /memory toggle mid-session takes effect.
                        if (!IsDeep()) { _started = 0; return; }

                        long now = Interlocked.Read(ref _activityTicks);
                        if (now != Interlocked.Read(ref _reflectedAtTicks))
                        {
                            bool wrote = await ReflectOnceAsync(chatClientFactory, ct);
                            if (wrote) Interlocked.Exchange(ref _reflectedAtTicks, now);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { /* best-effort; keep looping */ }

                    var cur = App.SwarmConfig?.ResolveReflection();
                    int delay = Math.Max(15, cur?.PollIntervalSeconds ?? seconds);
                    await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch { /* never surface */ }
            finally { _started = 0; }
        }, ct);
    }

    /// <summary>
    /// One distillation pass: read recent history, make a single light model call, parse and persist
    /// reflections. Returns true when at least one reflection was written. Public for tests.
    /// </summary>
    public static async Task<bool> ReflectOnceAsync(
        Func<string, IChatClient>? chatClientFactory, CancellationToken ct = default)
    {
        var history = HistoryProvider?.Invoke();
        if (history is null || history.Count == 0) return false;

        var cfg = App.SwarmConfig!.ResolveReflection();
        string? model = cfg.Model
            ?? App.SwarmConfig?.CompactionAgent?.Model
            ?? Common.LoadAgentModels().Values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(model) || chatClientFactory is null) return false;

        IChatClient client;
        try { client = chatClientFactory(model); }
        catch { return false; }

        var chatOpts = cfg.ModelOpts?.ToChatOptions();

        // PASS 1 (fast, tool-less): distill immediate reflections + optional DIG requests from the
        // live tail. These are written + injectable right away so they help on the very next turn.
        string raw = await DistillRawAsync(history, client, chatOpts, ct);
        var reflections = Parse(raw);

        bool any = false;
        foreach (var r in reflections)
            any |= await PersistAsync(r, ct);

        // PASS 2 (deep, read-only tools): fires ONLY when a dig is warranted - either Pass 1 emitted
        // a DIG line, or the cheap heuristic detector saw a concrete cue (path / file / "where is" /
        // error) in the latest user message. Both gates, whichever trips. Bounded, best-effort.
        var digs = ParseDigs(raw);
        var cue = DetectCue(LatestUserText(history));
        if (cue is not null && !digs.Any(d => d.Equals(cue, StringComparison.OrdinalIgnoreCase)))
            digs.Add(cue);

        if (digs.Count > 0)
        {
            // Cap the number of digs per tick so a noisy Pass 1 can't fan out unboundedly (configurable).
            int maxDigs;
            try { maxDigs = Math.Max(1, cfg.MaxDigsPerTick); } catch { maxDigs = 2; }
            foreach (var target in digs.Take(maxDigs))
            {
                var found = await DeepDigAsync(target, history, chatClientFactory, model, chatOpts, ct);
                foreach (var r in found)
                    any |= await PersistAsync(r, ct);
            }
        }

        if (any) _lastReflection = DateTimeOffset.UtcNow;
        return any;
    }

    private static async Task<bool> PersistAsync(Reflection r, CancellationToken ct)
    {
        if (ReflectionStore.Append(r) is null) return false;
        await ReflectionStore.MirrorAsync(r, ct);   // best-effort accelerator mirror
        return true;
    }

    private static string? LatestUserText(IReadOnlyList<ChatMessage> history)
    {
        for (int i = history.Count - 1; i >= 0; i--)
            if (history[i].Role == ChatRole.User)
                return history[i].Text;
        return null;
    }

    /// <summary>
    /// Pass 1: build the distillation prompt (grounded by BRAIN.md + MEMORY.md so reflections track
    /// the real working world), call the model ONCE (no tools), and return the RAW pipe-delimited
    /// output (reflection lines + optional DIG lines). Best-effort: returns "" on any failure.
    /// </summary>
    public static async Task<string> DistillRawAsync(
        IReadOnlyList<ChatMessage> history,
        IChatClient client,
        ChatOptions? chatOptions = null,
        CancellationToken ct = default)
    {
        try
        {
            var transcript = new StringBuilder();
            // Only the recent tail keeps the call cheap (window is configurable via historyWindow).
            int window;
            try { window = Math.Max(4, App.SwarmConfig?.ResolveReflection().HistoryWindow ?? 30); }
            catch { window = 30; }
            int start = Math.Max(0, history.Count - window);
            for (int i = start; i < history.Count; i++)
            {
                var m = history[i];
                string role = m.Role == ChatRole.User ? "User" : "Agent";
                var text = m.Text ?? string.Empty;
                if (text.Length > 1200) text = text[..1200] + " ...";
                transcript.AppendLine($"[{role}]: {text}");
            }
            if (transcript.Length == 0) return string.Empty;

            var system = new StringBuilder();
            system.AppendLine("You are the background reflection gatherer for an AI coding agent's DEEP MEMORY.");
            system.AppendLine("Observe the recent session and distill a FEW durable, high-signal reflections:");
            system.AppendLine("verbal failure-notes, decisions, user preferences, concrete facts (paths, ids,");
            system.AppendLine("values, file/function names), and the WHY behind a choice - whatever a future turn");
            system.AppendLine("would need. Skip transient chatter, secrets, and anything already obvious.");
            system.AppendLine();
            system.AppendLine("Ground your judgement in the agent's existing BRAIN.md / MEMORY.md context below;");
            system.AppendLine("do NOT re-state what is already recorded there - only NEW or changed lessons.");
            system.AppendLine();
            system.AppendLine("Output reflections, ONE per line, EXACTLY in this pipe format:");
            system.AppendLine("  <importance 0.0-1.0>|<role: lead|shared|AgentName>|<reflection>");
            system.AppendLine("Make each reflection SELF-CONTAINED and specific - a sentence or two is fine, and");
            system.AppendLine("INCLUDE the concrete detail (the actual path / id / value), not just that one exists.");
            system.AppendLine("A budget-capped selection of these is injected verbatim into the agent later, so a");
            system.AppendLine("richer note is more useful than a terse stub. Use \\n inside a reflection for a short");
            system.AppendLine("second line if needed; keep it to one logical lesson per line.");
            system.AppendLine();
            system.AppendLine("ADDITIONALLY: if the user is trying to FIND or NAIL DOWN something concrete that");
            system.AppendLine("is NOT fully answered in the context above - a file path, a config location, an");
            system.AppendLine("error/issue, a prior decision - emit a dig request so a read-only investigator can");
            system.AppendLine("chase it in the filesystem / memory stores. One per line, this exact format:");
            system.AppendLine("  DIG|<concise description of exactly what to locate>");
            system.AppendLine();
            system.AppendLine("Output nothing at all if there is nothing durable to record and nothing to dig for.");

            string grounding = ReadGrounding();
            if (grounding.Length > 0)
            {
                system.AppendLine();
                system.AppendLine("=== EXISTING MEMORY (for grounding; do not duplicate) ===");
                system.AppendLine(grounding);
            }

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, system.ToString()),
                new(ChatRole.User, transcript.ToString())
            };
            var response = await client.GetResponseAsync(messages, chatOptions, ct);
            return response.Text ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Pass 2: a read-only investigator chases a single dig target with the <see cref="ReflectionTools"/>
    /// (grep + read + store-query - NO write surface). It runs the built-in function-invocation loop
    /// (CreateChatClient wires it) bounded by MaxToolIterationsPerTurn, then emits one-line reflections
    /// (provenance "dig") capturing the concrete finding + a path pointer. Best-effort: returns empty
    /// on any failure. Reflections are role "shared" so they surface to the lead and relevant agents.
    /// </summary>
    public static async Task<List<Reflection>> DeepDigAsync(
        string target,
        IReadOnlyList<ChatMessage> history,
        Func<string, IChatClient>? chatClientFactory,
        string model,
        ChatOptions? chatOptions = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(target) || chatClientFactory is null) return new();
        try
        {
            IChatClient client = chatClientFactory(model);

            var system = new StringBuilder();
            system.AppendLine("You are a READ-ONLY memory investigator for an AI coding agent's deep memory.");
            system.AppendLine("The user is trying to locate or nail down something concrete. Using ONLY the");
            system.AppendLine("read-only tools provided (reflect_grep, reflect_read_file, reflect_list_dir,");
            system.AppendLine("reflect_query_store), find the specific answer. Be efficient: a couple of focused");
            system.AppendLine("tool calls, not an exhaustive crawl. You cannot write or modify anything.");
            system.AppendLine();
            system.AppendLine("TARGET TO LOCATE:");
            system.AppendLine($"  {target}");
            system.AppendLine();
            system.AppendLine("When done, output the finding as reflection lines, ONE per line, EXACTLY:");
            system.AppendLine("  <importance 0.0-1.0>|shared|<one-line finding, include the concrete path/value>");
            system.AppendLine("If you could not find it, output nothing. Do NOT narrate your search.");

            // A short slice of recent context helps disambiguate the target.
            var ctxTail = new StringBuilder();
            int start = Math.Max(0, history.Count - 6);
            for (int i = start; i < history.Count; i++)
            {
                var t = history[i].Text ?? "";
                if (t.Length > 600) t = t[..600] + " ...";
                ctxTail.AppendLine($"[{(history[i].Role == ChatRole.User ? "User" : "Agent")}]: {t}");
            }

            var opts = chatOptions is null ? new ChatOptions() : chatOptions.Clone();
            opts.Tools = ReflectionTools.Build().ToList();

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, system.ToString()),
                new(ChatRole.User, "Recent context for disambiguation:\n" + ctxTail)
            };
            var response = await client.GetResponseAsync(messages, opts, ct);
            var found = Parse(response.Text ?? string.Empty);
            foreach (var r in found) r.Provenance = "dig";
            return found;
        }
        catch { return new(); }
    }

    /// <summary>Parse the pipe-delimited reflection lines. Public for tests.</summary>
    public static List<Reflection> Parse(string text)
    {
        var results = new List<Reflection>();
        if (string.IsNullOrWhiteSpace(text)) return results;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split('|');
            if (parts.Length < 3) continue;
            if (!double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var importance))
                continue;
            var role = parts[1].Trim();
            var content = string.Join("|", parts.Skip(2)).Trim();
            if (content.Length == 0) continue;
            results.Add(new Reflection
            {
                Importance = Math.Clamp(importance, 0.0, 1.0),
                Role = role.Length == 0 ? "shared" : role,
                Content = content,
                Provenance = "gatherer",
                Timestamp = DateTimeOffset.UtcNow
            });
        }
        return results;
    }

    /// <summary>Extract DIG|&lt;target&gt; requests from the Pass-1 output. Public for tests.</summary>
    public static List<string> ParseDigs(string text)
    {
        var digs = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return digs;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("DIG|", StringComparison.OrdinalIgnoreCase)) continue;
            var target = line.Substring(4).Trim();
            if (target.Length > 0) digs.Add(target);
        }
        return digs;
    }

    /// <summary>
    /// Heuristic cue detector: cheap, no-LLM pre-filter over the latest user message. Returns a
    /// dig target when the message looks like the user is hunting something concrete (a path, an
    /// error, a "where is X" question, a file reference) even if Pass 1 did not flag it. Null = no cue.
    /// </summary>
    public static string? DetectCue(string? latestUserMessage)
    {
        if (string.IsNullOrWhiteSpace(latestUserMessage)) return null;
        var msg = latestUserMessage.Trim();
        if (msg.Length < 4) return null;
        var lower = msg.ToLowerInvariant();

        bool hasPath = System.Text.RegularExpressions.Regex.IsMatch(msg,
            @"(?:[A-Za-z]:\\|\\\\|/)[\w.\-\\/]{3,}");                 // win drive, UNC, or unix path
        bool hasFileRef = System.Text.RegularExpressions.Regex.IsMatch(msg,
            @"\b[\w.\-]+\.(?:cs|md|json|jsonc|py|js|ts|ya?ml|toml|ini|cfg|log|csproj|sh|ps1|txt)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        bool hasRecallPhrase =
            lower.Contains("where is") || lower.Contains("where's") || lower.Contains("where was") ||
            lower.Contains("where did") || lower.Contains("remember") || lower.Contains("recall") ||
            lower.Contains("find the") || lower.Contains("locate") || lower.Contains("which file") ||
            lower.Contains("that path") || lower.Contains("nail down") || lower.Contains("track down") ||
            lower.Contains("what was the");
        bool hasError = System.Text.RegularExpressions.Regex.IsMatch(msg,
            @"\b(?:error|exception|traceback|stack ?trace|CS\d{3,}|errno|fail(?:ed|ure)?)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (hasPath || hasFileRef || hasRecallPhrase || hasError)
        {
            var cue = msg.Length > 240 ? msg[..240] + " ..." : msg;
            return cue;
        }
        return null;
    }

    private static string ReadGrounding()
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var f in new[] { ContextCap.BrainFile, ContextCap.MemoryFile })
            {
                var p = Path.Combine(PlatformContext.ContextDirectory, f);
                if (!File.Exists(p)) continue;
                var c = File.ReadAllText(p);
                if (c.Length > 6000) c = c[..6000] + " ...";
                sb.AppendLine($"--- {f} ---");
                sb.AppendLine(c);
            }
            return sb.ToString();
        }
        catch { return string.Empty; }
    }

    /// <summary>Status snapshot for the /memory command.</summary>
    public static (bool deep, string model, int budget, int poll, double floor, string scope,
        int max, int historyWindow, int count, DateTimeOffset? last, bool chroma, bool kg) Status()
    {
        var cfg = App.SwarmConfig?.ResolveReflection() ?? new ReflectionConfig();
        return (
            cfg.IsDeep,
            cfg.Model ?? App.SwarmConfig?.CompactionAgent?.Model ?? "(orchestrator default)",
            cfg.InjectTokenBudget,
            cfg.PollIntervalSeconds,
            cfg.RelevanceFloor,
            cfg.Scope,
            cfg.MaxReflections,
            cfg.HistoryWindow,
            ReflectionStore.Count(),
            ReflectionStore.LastWrite(),
            ReflectionStore.ChromaAvailable(),
            ReflectionStore.KgAvailable());
    }
}
