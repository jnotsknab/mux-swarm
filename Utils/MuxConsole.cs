using Spectre.Console;

namespace MuxSwarm.Utils;

/// <summary>
/// Centralized console output for the entire MUX-SWARM application.
/// Interactive mode: Spectre.Console rich rendering (panels, rules, tables, colors).
/// Stdio mode:       Plain tagged text with zero ANSI codes for machine parsing.
///
/// Every file in the project should use MuxConsole instead of Console.Write/WriteLine.
/// </summary>
public static class MuxConsole
{
    // ─────────────────────────────────────────────────────────────
    // MODE FLAG — set once at startup from CLI args, never touched again
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// When true, all output is plain prefixed text suitable for piping / parsing.
    /// Set this from App.ParseArgs() when --stdio is present.
    /// </summary>
    public static bool StdioMode { get; set; } = false;

    // ─────────────────────────────────────────────────────────────
    // CONSOLE LOCK — coordinates ThinkingIndicator with streaming output
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Shared lock to prevent the thinking indicator's \r-based rendering
    /// from interleaving with streamed text output, which causes garbled lines.
    /// Used by all writers + ThinkingIndicator.
    /// </summary>
    internal static readonly object ConsoleLock = new();

    /// <summary>Currently running thinking indicator, if any.</summary>
    private static ThinkingIndicator? _activeIndicator;

    /// <summary>
    /// True while an agent/model is streaming output. When true, we do NOT clear
    /// a spinner line on every WriteStream() token/chunk.
    /// </summary>
    private static bool _isStreaming;

    // ─────────────────────────────────────────────────────────────
    // PALETTE — soft, muted, comforting
    // ─────────────────────────────────────────────────────────────

    /// <summary>Exposed for callers that need to build custom markup with the prompt color.</summary>
    public const string PromptColor = "#E0E0E0";

    private static class C
    {
        public const string Step = "#64B4DC";
        public const string Success = "#78C88C";
        public const string Warning = "#D4A054";
        public const string Error = "#D46C6C";
        public const string Info = "#909090";
        public const string Muted = "#787878";
        public const string Accent = "#64B4DC";
        public const string Prompt = "#B0B0B0";
        public const string Banner = "#64B4DC";
        public const string Agent = "#8FB8D4";
    }

    // ─────────────────────────────────────────────────────────────
    // CORE WRITE GUARD
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Ensures all console writes are serialized and do not interleave with the
    /// \r-based thinking indicator.
    ///
    /// Fully stops the indicator before writing (unless we are in streaming mode)
    /// to prevent it from re-rendering on a wrong line after newline-producing writes.
    /// The indicator will be recreated by BeginThinking when next needed.
    /// </summary>
    private static void WithConsole(Action write, bool clearIndicator = true)
    {
        lock (ConsoleLock)
        {
            if (clearIndicator && !_isStreaming && _activeIndicator is not null)
            {
                // Fully stop (clear + cancel) the indicator so its background loop
                // doesn't re-render on a different line after our newline-producing write.
                StopActiveIndicator_NoLock();
            }

            write();
        }
    }


    /// <summary>
    /// Stops and detaches the active indicator (caller must hold ConsoleLock).
    /// Uses CancelNoWait() to avoid deadlock since we already hold the lock.
    /// ClearNow_NoLock sets _clearedExternally so the loop's deferred clear is suppressed.
    /// </summary>
    private static void StopActiveIndicator_NoLock()
    {
        if (_activeIndicator is null) return;

        var ind = _activeIndicator;
        _activeIndicator = null;

        try
        {
            if (ind.HasRendered)
                ind.ClearNow_NoLock();  // sets _clearedExternally = true
        }
        catch { /* ignore */ }

        // Cancel without waiting — we hold ConsoleLock so we can't block on the loop.
        // The loop will see _clearedExternally and skip its own clear.
        ind.CancelNoWait();
    }

    // ─────────────────────────────────────────────────────────────
    // BANNER & STRUCTURE
    // ─────────────────────────────────────────────────────────────

    /// <summary>Render the setup banner — a clean horizontal rule with title.</summary>
    public static void WriteBanner(string title = "MUX-SWARM SETUP")
    {
        WithConsole(() =>
        {
            if (StdioMode)
            {
                Console.WriteLine($"=== {title} ===");
                return;
            }

            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[bold {C.Banner}]{Esc(title)}[/]")
                .RuleStyle(new Style(Color.Grey))
                .LeftJustified());
            AnsiConsole.WriteLine();
        });
    }

    // ─────────────────────────────────────────────────────────────
    // THINKING INDICATOR (factory)
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates and starts a thinking indicator for the given agent.
    /// The indicator shows an animated spinner with an updatable status phrase.
    ///
    /// Call <see cref="ThinkingIndicator.UpdateStatus"/> to reflect real activity
    /// (e.g. "calling Filesystem_ReadFile"). Dispose when streaming begins.
    ///
    /// In StdioMode, returns a no-op indicator that does nothing.
    /// </summary>
    public static ThinkingIndicator BeginThinking(string agentName)
    {
        if (StdioMode)
            return new ThinkingIndicator(_ => { }, _ => { }, ConsoleLock);

        // If we're streaming, don't start a spinner.
        lock (ConsoleLock)
        {
            if (_isStreaming)
                return new ThinkingIndicator(_ => { }, _ => { }, ConsoleLock);

            // Stop any existing one cleanly
            StopActiveIndicator_NoLock();
        }

        var indicator = new ThinkingIndicator(
            renderRaw: raw =>
            {
                // IMPORTANT: keep this consistent; don't mix Console.Write + AnsiConsole
                // for the same transient \r-based line. Raw console write is predictable.
                Console.Write(raw);
            },
            clearLine: maxLen =>
            {
                Console.Write("\r" + new string(' ', maxLen) + "\r");
            },
            consoleLock: ConsoleLock
        );

        lock (ConsoleLock)
        {
            _activeIndicator = indicator;
        }

        indicator.Start(agentName);
        return indicator;
    }

    /// <summary>
    /// Stop a thinking indicator and clear active reference if it matches.
    /// Prefer this over calling Dispose() directly.
    /// </summary>
    public static void EndThinking(ThinkingIndicator indicator)
    {
        lock (ConsoleLock)
        {
            try
            {
                if (indicator.HasRendered)
                    indicator.ClearNow_NoLock();  // sets _clearedExternally = true
            }
            catch { /* ignore */ }

            // Cancel without waiting — we hold ConsoleLock so can't block on the loop.
            indicator.CancelNoWait();

            if (ReferenceEquals(_activeIndicator, indicator))
                _activeIndicator = null;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // STREAMING CONTROL (IMPORTANT)
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Call ONCE right before model output starts streaming.
    /// This clears/stops the thinking indicator so it doesn't fight streamed output.
    /// </summary>
    public static void BeginStreaming()
    {
        lock (ConsoleLock)
        {
            _isStreaming = true;
            StopActiveIndicator_NoLock();
        }
    }

    /// <summary>
    /// Call when streaming is done (after final newline/flush).
    /// </summary>
    public static void EndStreaming()
    {
        lock (ConsoleLock)
        {
            if (!_isStreaming)
                return;

            _isStreaming = false;

            // Ensure the next "real" output (rules, prompts, session lines) starts cleanly.
            Console.WriteLine();
            try { Console.Out.Flush(); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Transitions from streaming mode back to thinking mode.
    /// Ends the current streaming session (newline + flush), then creates and
    /// starts a fresh ThinkingIndicator for the given agent.
    ///
    /// Use this in the streaming loop when a tool call arrives after text has
    /// already been streamed — the indicator reappears below the latest output
    /// so the user can see the agent is still working.
    ///
    /// Returns the new indicator (or a no-op in StdioMode/prodMode).
    /// </summary>
    public static ThinkingIndicator ResumeThinking(string agentName)
    {
        lock (ConsoleLock)
        {
            if (_isStreaming)
            {
                _isStreaming = false;
                Console.WriteLine();
                try { Console.Out.Flush(); } catch { /* ignore */ }
            }
        }

        return BeginThinking(agentName);
    }

    /// <summary>
    /// Stream a text chunk (no newline, no markup).
    /// NOTE: Does NOT clear the indicator per token (we clear once in BeginStreaming()).
    /// </summary>
    public static void WriteStream(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // No indicator clearing here (by design)
        WithConsole(() =>
        {
            if (StdioMode)
                Console.Write(text);
            else
                AnsiConsole.Markup(Esc(text));
        }, clearIndicator: false);
    }

    /// <summary>Render a numbered step header as a subtle rule.</summary>
    public static void WriteStep(int number, string title)
    {
        WithConsole(() =>
        {
            if (StdioMode)
            {
                Console.WriteLine($"\n=== Step {number}: {title} ===\n");
                return;
            }

            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[{C.Step}]Step {number}[/]  [{C.Prompt}]{Esc(title)}[/]")
                .RuleStyle(new Style(Color.Grey35))
                .LeftJustified());
            AnsiConsole.WriteLine();
        });
    }

    /// <summary>Render a section divider (unnumbered) — lighter than a step.</summary>
    public static void WriteRule(string? label = null)
    {
        WithConsole(() =>
        {
            if (StdioMode)
            {
                Console.WriteLine(label != null ? $"--- {label} ---" : "---");
                return;
            }

            var rule = label != null
                ? new Rule($"[{C.Muted}]{Esc(label)}[/]").RuleStyle(new Style(Color.Grey23))
                : new Rule().RuleStyle(new Style(Color.Grey23));

            AnsiConsole.Write(rule.LeftJustified());
        });
    }

    // ─────────────────────────────────────────────────────────────
    // MESSAGE OUTPUT
    // ─────────────────────────────────────────────────────────────

    public static void WriteSuccess(string message)
    {
        WithConsole(() =>
        {
            if (StdioMode) { Console.WriteLine($"[OK] {message}"); return; }
            AnsiConsole.MarkupLine($"  [{C.Success}]✓[/] [{C.Prompt}]{Esc(message)}[/]");
        });
    }

    public static void WriteWarning(string message)
    {
        WithConsole(() =>
        {
            if (StdioMode) { Console.WriteLine($"[WARN] {message}"); return; }
            AnsiConsole.MarkupLine($"  [{C.Warning}]![/] [{C.Warning}]{Esc(message)}[/]");
        });
    }

    public static void WriteError(string message)
    {
        WithConsole(() =>
        {
            if (StdioMode) { Console.WriteLine($"[ERROR] {message}"); return; }
            AnsiConsole.MarkupLine($"  [{C.Error}]x[/] [{C.Error}]{Esc(message)}[/]");
        });
    }

    public static void WriteInfo(string message)
    {
        WithConsole(() =>
        {
            if (StdioMode) { Console.WriteLine($"[INFO] {message}"); return; }
            AnsiConsole.MarkupLine($"  [{C.Info}]{Esc(message)}[/]");
        });
    }

    public static void WriteMuted(string message)
    {
        WithConsole(() =>
        {
            if (StdioMode) { Console.WriteLine(message); return; }
            AnsiConsole.MarkupLine($"  [{C.Muted}]{Esc(message)}[/]");
        });
    }

    /// <summary>Plain body text — not prefixed, not dimmed. For instructions, descriptions.</summary>
    public static void WriteBody(string message)
    {
        WithConsole(() =>
        {
            if (StdioMode) { Console.WriteLine(message); return; }
            AnsiConsole.MarkupLine($"  [{C.Prompt}]{Esc(message)}[/]");
        });
    }

    /// <summary>Write raw Spectre markup (interactive only). Stdio falls back to plain text.</summary>
    public static void WriteMarkup(string spectreMarkup, string? stdioFallback = null)
    {
        WithConsole(() =>
        {
            if (StdioMode) { Console.WriteLine(stdioFallback ?? StripMarkup(spectreMarkup)); return; }
            AnsiConsole.MarkupLine(spectreMarkup);
        });
    }

    /// <summary>Write inline (no newline) — for prompts, partial lines. No indent.</summary>
    public static void WriteInline(string spectreMarkup, string? stdioFallback = null)
    {
        WithConsole(() =>
        {
            if (StdioMode) { Console.Write(stdioFallback ?? StripMarkup(spectreMarkup)); return; }
            AnsiConsole.Markup(spectreMarkup);
        });
    }

    /// <summary>Blank line.</summary>
    public static void WriteLine()
    {
        WithConsole(() =>
        {
            if (StdioMode) Console.WriteLine();
            else AnsiConsole.WriteLine();
        });
    }
    
    /// <summary>Prompt for a text value. Returns trimmed input.</summary>
    public static string Prompt(string message, string? defaultValue = null)
    {
        // Stop spinner before blocking for user input
        lock (ConsoleLock)
        {
            StopActiveIndicator_NoLock();
        }

        if (StdioMode)
        {
            if (defaultValue != null)
                Console.Write($"{message} [{defaultValue}]: ");
            else
                Console.Write($"{message}: ");

            var input = Console.ReadLine()?.Trim() ?? string.Empty;
            return string.IsNullOrEmpty(input) && defaultValue != null ? defaultValue : input;
        }

        var prompt = new TextPrompt<string>($"  [{C.Prompt}]{Esc(message)}[/]")
            .PromptStyle(new Style(Color.White));

        if (defaultValue != null)
            prompt.DefaultValue(defaultValue).DefaultValueStyle(new Style(Color.Grey));
        else
            prompt.AllowEmpty();

        return AnsiConsole.Prompt(prompt).Trim();
    }

    /// <summary>Prompt for a secret (masked input).</summary>
    public static string PromptSecret(string message)
    {
        lock (ConsoleLock)
        {
            StopActiveIndicator_NoLock();
        }

        if (StdioMode)
        {
            Console.Write($"{message}: ");
            return Console.ReadLine()?.Trim() ?? string.Empty;
        }

        return AnsiConsole.Prompt(
            new TextPrompt<string>($"  [{C.Prompt}]{Esc(message)}[/]")
                .PromptStyle(new Style(Color.White))
                .Secret());
    }

    /// <summary>Yes/No confirmation. Returns true for yes.</summary>
    public static bool Confirm(string message, bool defaultValue = true)
    {
        lock (ConsoleLock)
        {
            StopActiveIndicator_NoLock();
        }

        if (StdioMode)
        {
            var hint = defaultValue ? "[Y/n]" : "[y/N]";
            Console.Write($"{message} {hint}: ");
            var input = Console.ReadLine()?.Trim().ToLowerInvariant() ?? string.Empty;

            return string.IsNullOrEmpty(input) ? defaultValue : input.StartsWith('y');
        }

        return AnsiConsole.Confirm($"  [{C.Prompt}]{Esc(message)}[/]", defaultValue);
    }

    /// <summary>Selection prompt — pick one from a list.</summary>
    public static string Select(string title, IEnumerable<string> choices)
    {
        lock (ConsoleLock)
        {
            StopActiveIndicator_NoLock();
        }

        if (StdioMode)
        {
            var list = choices.ToList();
            Console.WriteLine(title);
            for (int i = 0; i < list.Count; i++)
                Console.WriteLine($"  [{i + 1}] {list[i]}");
            Console.Write("Choice: ");

            var input = Console.ReadLine()?.Trim() ?? "1";
            if (int.TryParse(input, out int idx) && idx >= 1 && idx <= list.Count)
                return list[idx - 1];
            return list[0];
        }

        return AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"  [{C.Prompt}]{Esc(title)}[/]")
                .HighlightStyle(new Style(Color.White, decoration: Decoration.Bold))
                .AddChoices(choices));
    }

    /// <summary>Multi-select prompt — pick one or more.</summary>
    public static List<string> MultiSelect(string title, IEnumerable<string> choices)
    {
        lock (ConsoleLock)
        {
            StopActiveIndicator_NoLock();
        }

        if (StdioMode)
        {
            var list = choices.ToList();
            Console.WriteLine(title);
            for (int i = 0; i < list.Count; i++)
                Console.WriteLine($"  [{i + 1}] {list[i]}");
            Console.Write("Choices (comma-separated numbers): ");

            var input = Console.ReadLine()?.Trim() ?? "";
            var selected = new List<string>();
            foreach (var part in input.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part.Trim(), out int idx) && idx >= 1 && idx <= list.Count)
                    selected.Add(list[idx - 1]);
            }
            return selected.Count > 0 ? selected : new List<string> { list[0] };
        }

        return AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title($"  [{C.Prompt}]{Esc(title)}[/]")
                .HighlightStyle(new Style(Color.White, decoration: Decoration.Bold))
                .AddChoices(choices))
            .ToList();
    }

    // ─────────────────────────────────────────────────────────────
    // TABLES & PANELS
    // ─────────────────────────────────────────────────────────────

    /// <summary>Render a key/value summary table (e.g. setup summary).</summary>
    public static void WriteSummaryTable(string title, IEnumerable<(string Key, string Value)> rows)
    {
        WithConsole(() =>
        {
            if (StdioMode)
            {
                Console.WriteLine($"\n--- {title} ---");
                foreach (var (key, value) in rows)
                    Console.WriteLine($"  {key}: {value}");
                Console.WriteLine();
                return;
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderStyle(new Style(Color.Grey35))
                .Title($"[{C.Step}]{Esc(title)}[/]")
                .AddColumn(new TableColumn($"[{C.Muted}]Setting[/]").PadRight(2))
                .AddColumn(new TableColumn($"[{C.Muted}]Value[/]"));

            foreach (var (key, value) in rows)
                table.AddRow($"[{C.Info}]{Esc(key)}[/]", $"[{C.Prompt}]{Esc(value)}[/]");

            AnsiConsole.WriteLine();
            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
        });
    }

    /// <summary>Render a bordered panel with a title — useful for grouped info.</summary>
    public static void WritePanel(string title, string content)
    {
        WithConsole(() =>
        {
            if (StdioMode)
            {
                Console.WriteLine($"[{title}]");
                Console.WriteLine(content);
                return;
            }

            AnsiConsole.Write(new Panel($"[{C.Prompt}]{Esc(content)}[/]")
                .Header($"[{C.Step}]{Esc(title)}[/]")
                .Border(BoxBorder.Rounded)
                .BorderStyle(new Style(Color.Grey35))
                .Padding(1, 0));
        });
    }

    // ─────────────────────────────────────────────────────────────
    // SPINNERS & PROGRESS
    // ─────────────────────────────────────────────────────────────

    /// <summary>Run an action with a spinner animation. Stdio mode just prints start/done.</summary>
    public static void WithSpinner(string message, Action work)
    {
        // Fully stop the thinking indicator so its background loop doesn't
        // fight with Spectre's Status spinner (which also uses \r-based rendering).
        lock (ConsoleLock)
        {
            StopActiveIndicator_NoLock();
        }

        if (StdioMode)
        {
            Console.WriteLine($"[...] {message}");
            work();
            Console.WriteLine($"[DONE] {message}");
            return;
        }

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(new Style(Color.Grey))
            .Start($"[{C.Info}]{Esc(message)}[/]", _ => work());
    }

    /// <summary>Run an async action with a spinner animation.</summary>
    public static async Task WithSpinnerAsync(string message, Func<Task> work)
    {
        // Fully stop the thinking indicator so its background loop doesn't
        // fight with Spectre's Status spinner (which also uses \r-based rendering).
        lock (ConsoleLock)
        {
            StopActiveIndicator_NoLock();
        }

        if (StdioMode)
        {
            Console.WriteLine($"[...] {message}");
            await work();
            Console.WriteLine($"[DONE] {message}");
            return;
        }

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(new Style(Color.Grey))
            .StartAsync($"[{C.Info}]{Esc(message)}[/]", async _ => await work());
    }

    // ─────────────────────────────────────────────────────────────
    // AGENT OUTPUT
    // ─────────────────────────────────────────────────────────────

    /// <summary>Visual header before an agent starts streaming. Gives structure between turns.</summary>
    public static void WriteAgentTurnHeader(string agentName)
    {
        WithConsole(() =>
        {
            if (StdioMode) { Console.WriteLine($"[TURN] {agentName}"); return; }
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[{C.Agent}]{Esc(agentName)}[/]")
                .RuleStyle(new Style(Color.Grey23))
                .LeftJustified());
        });
    }

    /// <summary>Visual breathing room after an agent finishes streaming.</summary>
    public static void WriteAgentTurnFooter()
    {
        WithConsole(() =>
        {
            if (StdioMode) return;
            AnsiConsole.WriteLine();
        }, clearIndicator: false);
    }



    public static void WriteDelegation(string fromAgent, string toAgent, string task, int truncLength = 800)
    {
        WithConsole(() =>
        {
            if (StdioMode) { Console.WriteLine($"[DELEGATE] {fromAgent} -> {toAgent}: {task}"); return; }

            string truncTask = task.Length > truncLength ? task[..truncLength] + "..." : task;
            AnsiConsole.MarkupLine($"  [{C.Accent}]>>[/] [{C.Info}]{Esc(fromAgent)}[/] [{C.Muted}]->[/] [{C.Step}]{Esc(toAgent)}[/]");
            AnsiConsole.MarkupLine($"     [{C.Muted}]{Esc(truncTask)}[/]");
        });
    }

    public static void WriteToolCall(string agent, string tool, string? args = null)
    {
        WithConsole(() =>
        {
            if (StdioMode) { Console.WriteLine($"[TOOL] {agent}.{tool}{(args != null ? $" {args}" : "")}"); return; }
            AnsiConsole.MarkupLine($"  [{C.Muted}]--[/] [{C.Info}]{Esc(agent)}[/] [{C.Muted}]->[/] [{C.Accent}]{Esc(tool)}[/]");
        });
    }

    public static void WriteToolResult(string agent, string summary)
    {
        WithConsole(() =>
        {
            if (StdioMode) { Console.WriteLine($"[RESULT] {agent}: {summary}"); return; }

            string clean = CollapseWhitespace(summary);
            if (clean.Length > 120) clean = clean[..120] + "...";
            AnsiConsole.MarkupLine($"     [{C.Muted}]{Esc(clean)}[/]");
        });
    }

    public static void WriteTaskComplete(string agent, string summary)
    {
        WithConsole(() =>
        {
            if (StdioMode) { Console.WriteLine($"[COMPLETE] {agent}: {summary}"); return; }
            AnsiConsole.MarkupLine($"  [{C.Success}]✓[/] [{C.Step}]{Esc(agent)}[/] [{C.Muted}]completed[/]  [{C.Info}]{Esc(summary)}[/]");
        });
    }

    // ─────────────────────────────────────────────────────────────
    // INTERNAL HELPERS
    // ─────────────────────────────────────────────────────────────

    /// <summary>Escape text for Spectre markup (brackets must be doubled).</summary>
    private static string Esc(string text) => Markup.Escape(text);

    /// <summary>Rough strip of Spectre markup tags for stdio fallback.</summary>
    private static string StripMarkup(string markup)
    {
        var result = System.Text.RegularExpressions.Regex.Replace(markup, @"\[/?[^\]]*\]", "");
        return result.Trim();
    }

    /// <summary>Collapse runs of whitespace/newlines into single spaces for compact display.</summary>
    private static string CollapseWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");
    }

    public static void FlushAndNewline()
    {
        WithConsole(() =>
        {
            // Ensure we are on a clean new line boundary.
            Console.WriteLine();
            try { Console.Out.Flush(); } catch { /* ignore */ }
        });
    }
}