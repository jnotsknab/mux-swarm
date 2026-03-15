using System.Text;
using System.Text.Json;
using Spectre.Console;

namespace MuxSwarm.Utils;

/// <summary>
/// Centralized console output for the entire MUX-SWARM application.
/// Interactive mode: Spectre.Console rich rendering (panels, rules, tables, colors).
/// Stdio mode:       NDJSON (newline-delimited JSON) for machine parsing and pipeline integration.
///
/// Every file in the project should use MuxConsole instead of Console.Write/WriteLine.
/// </summary>
public static class MuxConsole
{
    /// <summary>
    /// When true, all output is NDJSON (one JSON object per line) suitable for piping / parsing.
    /// Set this from App.ParseArgs() when --stdio is present.
    /// </summary>
    public static bool StdioMode { get; set; } = false;

    /// <summary>
    /// Shared lock to prevent the thinking indicator's \r-based rendering
    /// from interleaving with streamed text output, which causes garbled lines.
    /// </summary>
    internal static readonly object ConsoleLock = new();


    /// <summary>
    /// Gets or sets the text reader used for console input.
    /// Defaults to <see cref="Console.In"/>. Set this property to redirect input from a different source
    /// such as a <see cref="StringReader"/> for testing or automated scenarios.
    /// </summary>
    public static TextReader InputOverride { get; set; } = Console.In;

    private static ThinkingIndicator? _activeIndicator;
    public static string? MultiLineDelimiter { get; set; }
    public static bool UsingDelimiter { get; set; }
    private static bool _isStreaming;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static void EmitJson(string type, object? data = null)
    {
        var payload = new Dictionary<string, object?> { ["type"] = type };
        if (data is Dictionary<string, object?> dict)
        {
            foreach (var kvp in dict)
                payload[kvp.Key] = kvp.Value;
        }
        else if (data is not null)
        {
            payload["message"] = data;
        }

        Console.WriteLine(JsonSerializer.Serialize(payload, _jsonOpts));
    }

    private static Dictionary<string, object?> D(params (string Key, object? Value)[] pairs)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var (key, value) in pairs)
            dict[key] = value;
        return dict;
    }

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

    private static void WithConsole(Action write, bool clearIndicator = true)
    {
        lock (ConsoleLock)
        {
            if (clearIndicator && !_isStreaming && _activeIndicator is not null)
                StopActiveIndicator_NoLock();

            write();
        }
    }

    private static void StopActiveIndicator_NoLock()
    {
        if (_activeIndicator is null) return;

        var ind = _activeIndicator;
        _activeIndicator = null;

        try
        {
            if (ind.HasRendered)
                ind.ClearNow_NoLock();
        }
        catch { /* ignore */ }

        ind.CancelNoWait();
    }
    
    public static string? ReadInput(CancellationToken ct = default)
    {
        if (UsingDelimiter && MultiLineDelimiter is not null)
        {
            var sb = new StringBuilder();
            while (true)
            {
                var line = StdinCancelMonitor.Instance?.ReadLine(ct) ?? Console.ReadLine();
                if (line is null || line.Trim() == MultiLineDelimiter) break;
                sb.AppendLine(line);
            }
            return sb.ToString().Trim();
        }

        return StdinCancelMonitor.Instance?.ReadLine(ct) ?? Console.ReadLine();
    }
    
    public static void WriteBanner(string title = "MUX-SWARM SETUP")
    {
        WithConsole(() =>
        {
            if (StdioMode)
            {
                EmitJson("banner", D(("title", title)));
                return;
            }

            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[bold {C.Banner}]{Esc(title)}[/]")
                .RuleStyle(new Style(Color.Grey))
                .LeftJustified());
            AnsiConsole.WriteLine();
        });
    }

    /// <summary>
    /// Creates and starts a thinking indicator for the given agent.
    /// In StdioMode, emits thinking_start/thinking_update/thinking_end NDJSON events
    /// so integrations can render loading/spinner UI.
    /// </summary>
    public static ThinkingIndicator BeginThinking(string agentName)
    {
        if (StdioMode)
        {
            EmitJson("thinking_start", D(("agent", agentName)));
            return new ThinkingIndicator(
                renderRaw: _ => { },
                clearLine: _ => { },
                consoleLock: ConsoleLock,
                onStatusUpdate: status => EmitJson("thinking_update", D(("agent", agentName), ("status", status))),
                onDispose: () => EmitJson("thinking_end", D(("agent", agentName)))
            );
        }

        lock (ConsoleLock)
        {
            if (_isStreaming)
                return new ThinkingIndicator(_ => { }, _ => { }, ConsoleLock);

            StopActiveIndicator_NoLock();
        }

        var indicator = new ThinkingIndicator(
            renderRaw: raw => Console.Write(raw),
            clearLine: maxLen => Console.Write("\r" + new string(' ', maxLen) + "\r"),
            consoleLock: ConsoleLock
        );

        lock (ConsoleLock)
        {
            _activeIndicator = indicator;
        }

        indicator.Start(agentName);
        return indicator;
    }

    public static void EndThinking(ThinkingIndicator indicator)
    {
        lock (ConsoleLock)
        {
            try
            {
                if (indicator.HasRendered)
                    indicator.ClearNow_NoLock();
            }
            catch { /* ignore */ }

            indicator.CancelNoWait();

            if (ReferenceEquals(_activeIndicator, indicator))
                _activeIndicator = null;
        }
    }

    public static void BeginStreaming()
    {
        lock (ConsoleLock)
        {
            _isStreaming = true;
            StopActiveIndicator_NoLock();
        }
    }

    public static void EndStreaming()
    {
        lock (ConsoleLock)
        {
            if (!_isStreaming)
                return;

            _isStreaming = false;

            if (StdioMode)
            {
                EmitJson("stream_end");
                return;
            }

            Console.WriteLine();
            try { Console.Out.Flush(); } catch { /* ignore */ }
        }
    }

    public static ThinkingIndicator ResumeThinking(string agentName)
    {
        lock (ConsoleLock)
        {
            if (_isStreaming)
            {
                _isStreaming = false;

                if (StdioMode)
                    EmitJson("stream_end");
                else
                {
                    Console.WriteLine();
                    try { Console.Out.Flush(); } catch { /* ignore */ }
                }
            }
        }

        return BeginThinking(agentName);
    }

    public static void WriteStream(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        WithConsole(() =>
        {
            if (StdioMode)
                EmitJson("stream", D(("text", text)));
            else
                AnsiConsole.Markup(Esc(text));
        }, clearIndicator: false);
    }

    public static void WriteStep(int number, string title)
    {
        WithConsole(() =>
        {
            if (StdioMode)
            {
                EmitJson("step", D(("number", number), ("title", title)));
                return;
            }

            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[{C.Step}]Step {number}[/]  [{C.Prompt}]{Esc(title)}[/]")
                .RuleStyle(new Style(Color.Grey35))
                .LeftJustified());
            AnsiConsole.WriteLine();
        });
    }

    public static void WriteRule(string? label = null)
    {
        WithConsole(() =>
        {
            if (StdioMode)
            {
                EmitJson("rule", label != null ? D(("label", label)) : null);
                return;
            }

            var rule = label != null
                ? new Rule($"[{C.Muted}]{Esc(label)}[/]").RuleStyle(new Style(Color.Grey23))
                : new Rule().RuleStyle(new Style(Color.Grey23));

            AnsiConsole.Write(rule.LeftJustified());
        });
    }

    public static void WriteSuccess(string message)
    {
        WithConsole(() =>
        {
            if (StdioMode) { EmitJson("success", message); return; }
            AnsiConsole.MarkupLine($"  [{C.Success}]✓[/] [{C.Prompt}]{Esc(message)}[/]");
        });
    }

    public static void WriteWarning(string message)
    {
        WithConsole(() =>
        {
            if (StdioMode) { EmitJson("warning", message); return; }
            AnsiConsole.MarkupLine($"  [{C.Warning}]![/] [{C.Warning}]{Esc(message)}[/]");
        });
    }

    public static void WriteError(string message)
    {
        WithConsole(() =>
        {
            if (StdioMode) { EmitJson("error", message); return; }
            AnsiConsole.MarkupLine($"  [{C.Error}]x[/] [{C.Error}]{Esc(message)}[/]");
        });
    }

    public static void WriteInfo(string message)
    {
        WithConsole(() =>
        {
            if (StdioMode) { EmitJson("info", message); return; }
            AnsiConsole.MarkupLine($"  [{C.Info}]{Esc(message)}[/]");
        });
    }

    public static void WriteMuted(string message)
    {
        WithConsole(() =>
        {
            if (StdioMode) { EmitJson("debug", message); return; }
            AnsiConsole.MarkupLine($"  [{C.Muted}]{Esc(message)}[/]");
        });
    }

    public static void WriteBody(string message)
    {
        WithConsole(() =>
        {
            if (StdioMode) { EmitJson("body", message); return; }
            AnsiConsole.MarkupLine($"  [{C.Prompt}]{Esc(message)}[/]");
        });
    }

    public static void WriteMarkup(string spectreMarkup, string? stdioFallback = null)
    {
        WithConsole(() =>
        {
            if (StdioMode) { EmitJson("markup", stdioFallback ?? StripMarkup(spectreMarkup)); return; }
            AnsiConsole.MarkupLine(spectreMarkup);
        });
    }

    public static void WriteInline(string spectreMarkup, string? stdioFallback = null)
    {
        WithConsole(() =>
        {
            if (StdioMode) { EmitJson("prompt", stdioFallback ?? StripMarkup(spectreMarkup)); return; }
            AnsiConsole.Markup(spectreMarkup);
        });
    }

    public static void WriteLine()
    {
        WithConsole(() =>
        {
            if (StdioMode) return;
            AnsiConsole.WriteLine();
        });
    }

    public static string Prompt(string message, string? defaultValue = null)
    {
        lock (ConsoleLock)
        {
            StopActiveIndicator_NoLock();
        }
        
        if (UsingDelimiter && MultiLineDelimiter is not null)
        {
            if (StdioMode)
                EmitJson("input_request", D(("prompt", message), ("default", defaultValue), ("delimiter", MultiLineDelimiter)));

            var sb = new StringBuilder();
            var reader = StdioMode ? InputOverride : Console.In;
            while (reader.ReadLine() is { } line)
            {
                if (line.Trim() == MultiLineDelimiter) break;
                sb.AppendLine(line);
            }
            var result = sb.ToString().Trim();
            return string.IsNullOrEmpty(result) && defaultValue != null ? defaultValue : result;
        }
        
        if (StdioMode)
        {
            EmitJson("input_request", D(("prompt", message), ("default", defaultValue)));
            var input = InputOverride.ReadLine()?.Trim() ?? string.Empty;
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

    public static string PromptSecret(string message)
    {
        lock (ConsoleLock)
        {
            StopActiveIndicator_NoLock();
        }

        if (StdioMode)
        {
            EmitJson("input_request", D(("prompt", message), ("secret", true)));
            return InputOverride.ReadLine()?.Trim() ?? string.Empty;
        }

        return AnsiConsole.Prompt(
            new TextPrompt<string>($"  [{C.Prompt}]{Esc(message)}[/]")
                .PromptStyle(new Style(Color.White))
                .Secret());
    }

    public static bool Confirm(string message, bool defaultValue = true)
    {
        lock (ConsoleLock)
        {
            StopActiveIndicator_NoLock();
        }

        if (StdioMode)
        {
            EmitJson("confirm_request", D(("prompt", message), ("default", defaultValue)));
            var input = InputOverride.ReadLine()?.Trim().ToLowerInvariant() ?? string.Empty;
            return string.IsNullOrEmpty(input) ? defaultValue : input.StartsWith('y');
        }

        return AnsiConsole.Confirm($"  [{C.Prompt}]{Esc(message)}[/]", defaultValue);
    }

    public static string Select(string title, IEnumerable<string> choices)
    {
        lock (ConsoleLock)
        {
            StopActiveIndicator_NoLock();
        }

        if (StdioMode)
        {
            var list = choices.ToList();
            EmitJson("select_request", D(("prompt", title), ("choices", list)));

            var input = InputOverride.ReadLine()?.Trim() ?? "1";
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

    public static List<string> MultiSelect(string title, IEnumerable<string> choices)
    {
        lock (ConsoleLock)
        {
            StopActiveIndicator_NoLock();
        }

        if (StdioMode)
        {
            var list = choices.ToList();
            EmitJson("multiselect_request", D(("prompt", title), ("choices", list)));

            var input = InputOverride.ReadLine()?.Trim() ?? "";
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

    public static void WriteSummaryTable(string title, IEnumerable<(string Key, string Value)> rows)
    {
        WithConsole(() =>
        {
            if (StdioMode)
            {
                var rowList = rows.Select(r => new Dictionary<string, object?> { ["key"] = r.Key, ["value"] = r.Value }).ToList();
                EmitJson("table", D(("title", title), ("rows", rowList)));
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

    public static void WritePanel(string title, string content)
    {
        WithConsole(() =>
        {
            if (StdioMode)
            {
                EmitJson("panel", D(("title", title), ("content", content)));
                return;
            }

            AnsiConsole.Write(new Panel($"[{C.Prompt}]{Esc(content)}[/]")
                .Header($"[{C.Step}]{Esc(title)}[/]")
                .Border(BoxBorder.Rounded)
                .BorderStyle(new Style(Color.Grey35))
                .Padding(1, 0));
        });
    }

    public static void WithSpinner(string message, Action work)
    {
        lock (ConsoleLock)
        {
            StopActiveIndicator_NoLock();
        }

        if (StdioMode)
        {
            EmitJson("task_start", message);
            work();
            EmitJson("task_done", message);
            return;
        }

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(new Style(Color.Grey))
            .Start($"[{C.Info}]{Esc(message)}[/]", _ => work());
    }

    public static async Task WithSpinnerAsync(string message, Func<Task> work)
    {
        lock (ConsoleLock)
        {
            StopActiveIndicator_NoLock();
        }

        if (StdioMode)
        {
            EmitJson("task_start", message);
            await work();
            EmitJson("task_done", message);
            return;
        }

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(new Style(Color.Grey))
            .StartAsync($"[{C.Info}]{Esc(message)}[/]", async _ => await work());
    }

    public static void WriteAgentTurnHeader(string agentName)
    {
        WithConsole(() =>
        {
            if (StdioMode) { EmitJson("agent_turn_start", D(("agent", agentName))); return; }
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[{C.Agent}]{Esc(agentName)}[/]")
                .RuleStyle(new Style(Color.Grey23))
                .LeftJustified());
        });
    }

    public static void WriteAgentTurnFooter()
    {
        WithConsole(() =>
        {
            if (StdioMode) { EmitJson("agent_turn_end"); return; }
            AnsiConsole.WriteLine();
        }, clearIndicator: false);
    }

    public static void WriteDelegation(string fromAgent, string toAgent, string task, int truncLength = 800)
    {
        WithConsole(() =>
        {
            if (StdioMode)
            {
                EmitJson("delegation", D(("from", fromAgent), ("to", toAgent), ("task", task)));
                return;
            }

            string truncTask = task.Length > truncLength ? task[..truncLength] + "..." : task;
            AnsiConsole.MarkupLine($"  [{C.Accent}]>>[/] [{C.Info}]{Esc(fromAgent)}[/] [{C.Muted}]->[/] [{C.Step}]{Esc(toAgent)}[/]");
            AnsiConsole.MarkupLine($"     [{C.Muted}]{Esc(truncTask)}[/]");
        });
    }

    public static void WriteToolCall(string agent, string tool, string? args = null)
    {
        WithConsole(() =>
        {
            if (StdioMode)
            {
                EmitJson("tool_call", D(("agent", agent), ("tool", tool), ("args", args)));
                return;
            }
            AnsiConsole.MarkupLine($"  [{C.Muted}]--[/] [{C.Info}]{Esc(agent)}[/] [{C.Muted}]->[/] [{C.Accent}]{Esc(tool)}[/]");
        });
    }

    public static void WriteToolResult(string agent, string summary)
    {
        WithConsole(() =>
        {
            if (StdioMode)
            {
                EmitJson("tool_result", D(("agent", agent), ("summary", summary)));
                return;
            }

            string clean = CollapseWhitespace(summary);
            if (clean.Length > 120) clean = clean[..120] + "...";
            AnsiConsole.MarkupLine($"     [{C.Muted}]{Esc(clean)}[/]");
        });
    }

    public static void WriteTaskComplete(string agent, string summary)
    {
        WithConsole(() =>
        {
            if (StdioMode)
            {
                EmitJson("task_complete", D(("agent", agent), ("summary", summary)));
                return;
            }
            AnsiConsole.MarkupLine($"  [{C.Success}]✓[/] [{C.Step}]{Esc(agent)}[/] [{C.Muted}]completed[/]  [{C.Info}]{Esc(summary)}[/]");
        });
    }

    private static string Esc(string text) => Markup.Escape(text);

    private static string StripMarkup(string markup)
    {
        var result = System.Text.RegularExpressions.Regex.Replace(markup, @"\[/?[^\]]*\]", "");
        return result.Trim();
    }

    private static string CollapseWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");
    }

    public static void FlushAndNewline()
    {
        WithConsole(() =>
        {
            if (StdioMode) return;
            Console.WriteLine();
            try { Console.Out.Flush(); } catch { /* ignore */ }
        });
    }
}