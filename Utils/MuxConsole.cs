using System.Text;
using System.Text.Json;
using MuxSwarm.State;
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
    public static string? MascotPath { get; set; }

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
        if (InputOverride != Console.In)
        {
            var line = InputOverride.ReadLine();
            if (line == null)
            {
                //exhausted workflow return to keyboard input
                InputOverride = Console.In;
                return Console.ReadLine();
            }
            return line;
        }

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
    
    public static string AskText(string question, string? defaultValue)
    {
        string response = MuxConsole.Prompt(question, defaultValue);
        return string.IsNullOrWhiteSpace(response)
            ? "User provided no input."
            : $"User response: {response}";
    }

    public static string AskSelect(string question, string? options)
    {
        var choices = ParseOptions(options);
        if (choices.Count == 0)
            return "Error: 'select' type requires at least one option in the options parameter.";

        string selected = MuxConsole.Select(question, choices);
        return $"User selected: {selected}";
    }

    public static string AskMultiSelect(string question, string? options)
    {
        var choices = ParseOptions(options);
        if (choices.Count == 0)
            return "Error: 'multi_select' type requires at least one option in the options parameter.";

        var selected = MuxConsole.MultiSelect(question, choices);
        return $"User selected: {string.Join(", ", selected)}";
    }
    
    public static List<string> ParseOptions(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToList();
    }
    
    /// <summary>
    /// Renders a splash screen: block-art title with ASCII mascot, version, and repo link.
    /// No external dependencies or image files required.
    /// </summary>
    public static void WriteSplashScreen(string version)
    {
        WithConsole(() =>
        {
            if (StdioMode)
            {
                EmitJson("splash", D(("version", version)));
                return;
            }

            AnsiConsole.WriteLine();

            if (AnsiConsole.Profile.Width < 56)
            {
                AnsiConsole.Write(new Rule($"[bold {C.Banner}]MUX-SWARM[/]  [{C.Muted}]v{Esc(version)}[/]")
                    .RuleStyle(new Style(Color.Grey35))
                    .LeftJustified());
            }
            else if (AnsiConsole.Profile.Width < 180)
            {
                string[] mux =
                {
                    "███╗   ███╗██╗   ██╗██╗  ██╗",
                    "████╗ ████║██║   ██║╚██╗██╔╝",
                    "██╔████╔██║██║   ██║ ╚███╔╝ ",
                    "██║╚██╔╝██║██║   ██║ ██╔██╗ ",
                    "██║ ╚═╝ ██║╚██████╔╝██╔╝ ██╗",
                    "╚═╝     ╚═╝ ╚═════╝ ╚═╝  ╚═╝",
                };

                string[] bot =
                {
                    @"   █     █ ",
                    @" ▄█████████▄ ",
                    @" █   ◠ ◠   █ ",
                    @" █    ◡    █ ",
                    @" █▄▄▄▄▄▄▄▄▄█ ",
                    @"  ▀▀▀▀▀▀▀▀▀  ",
                };

                int muxW = mux.Max(l => l.Length);
                var sb = new StringBuilder();

                for (int i = 0; i < mux.Length; i++)
                {
                    var muxLine = mux[i].PadRight(muxW + 4);
                    var botLine = i < bot.Length ? bot[i] : "";
                    sb.AppendLine($"[{C.Banner}]{Esc(muxLine)}[/][{C.Success}]{Esc(botLine)}[/]");
                }

                const string swarmArt =
                    "███████╗██╗    ██╗ █████╗ ██████╗ ███╗   ███╗\n" +
                    "██╔════╝██║    ██║██╔══██╗██╔══██╗████╗ ████║\n" +
                    "███████╗██║ █╗ ██║███████║██████╔╝██╔████╔██║\n" +
                    "╚════██║██║███╗██║██╔══██║██╔══██╗██║╚██╔╝██║\n" +
                    "███████║╚███╔███╔╝██║  ██║██║  ██║██║ ╚═╝ ██║\n" +
                    "╚══════╝ ╚══╝╚══╝ ╚═╝  ╚═╝╚═╝  ╚═╝╚═╝     ╚═╝";

                sb.AppendLine($"[{C.Banner}]{Esc(swarmArt)}[/]");
                sb.AppendLine();
                sb.AppendLine($"[{C.Step}]v{Esc(version)}[/]  [{C.Muted}]·[/]  [{C.Prompt}]CLI-native agentic swarm OS[/]");
                sb.Append($"[{C.Muted}][link=https://github.com/jnotsknab/mux-swarm]Check Out The Repo Here![/][/]  [{C.Muted}]·[/]  [{C.Muted}]Type /help for commands[/]");

                var panel = new Panel(sb.ToString())
                    .Border(BoxBorder.Rounded)
                    .BorderStyle(new Style(Color.Grey35))
                    .Padding(1, 1)
                    .Expand();

                AnsiConsole.Write(panel);
            }
            else
            {
                string[] mux =
                {
                    "███╗   ███╗██╗   ██╗██╗  ██╗",
                    "████╗ ████║██║   ██║╚██╗██╔╝",
                    "██╔████╔██║██║   ██║ ╚███╔╝ ",
                    "██║╚██╔╝██║██║   ██║ ██╔██╗ ",
                    "██║ ╚═╝ ██║╚██████╔╝██╔╝ ██╗",
                    "╚═╝     ╚═╝ ╚═════╝ ╚═╝  ╚═╝",
                };

                // swap in whatever mascot you landed on
                string[] bot =
                {
                    @"   █     █ ",
                    @" ▄█████████▄ ",
                    @" █   ◠ ◠   █ ",
                    @" █    ◡    █ ",
                    @" █▄▄▄▄▄▄▄▄▄█ ",
                    @"  ▀▀▀▀▀▀▀▀▀  ",
                };

                int muxW = mux.Max(l => l.Length);
                var left = new StringBuilder();

                for (int i = 0; i < mux.Length; i++)
                {
                    var muxLine = mux[i].PadRight(muxW + 4);
                    var botLine = i < bot.Length ? bot[i] : "";
                    left.AppendLine($"[{C.Banner}]{Esc(muxLine)}[/][{C.Success}]{Esc(botLine)}[/]");
                }

                const string swarmArt =
                    "███████╗██╗    ██╗ █████╗ ██████╗ ███╗   ███╗\n" +
                    "██╔════╝██║    ██║██╔══██╗██╔══██╗████╗ ████║\n" +
                    "███████╗██║ █╗ ██║███████║██████╔╝██╔████╔██║\n" +
                    "╚════██║██║███╗██║██╔══██║██╔══██╗██║╚██╔╝██║\n" +
                    "███████║╚███╔███╔╝██║  ██║██║  ██║██║ ╚═╝ ██║\n" +
                    "╚══════╝ ╚══╝╚══╝ ╚═╝  ╚═╝╚═╝  ╚═╝╚═╝     ╚═╝";

                left.AppendLine($"[{C.Banner}]{Esc(swarmArt)}[/]");
                left.AppendLine();
                left.AppendLine($"[{C.Step}]v{Esc(version)}[/]  [{C.Muted}]·[/]  [{C.Prompt}]CLI-native agentic swarm OS[/]");
                left.Append($"[{C.Muted}][link=https://github.com/jnotsknab/mux-swarm]Check Out The Repo Here![/][/]");

                var right = new StringBuilder();
                right.AppendLine($"[{C.Step}]Getting Started[/]");
                right.AppendLine($"[{C.Muted}]Pick a mode to begin, then enter your task[/]");
                right.AppendLine($"[{C.Muted}]────────────────────────────────────────────[/]");
                right.AppendLine();
                right.AppendLine($"  [{C.Prompt}]/swarm[/]       [{C.Muted}]Multi-agent orchestrated loop[/]");
                right.AppendLine($"  [{C.Prompt}]/pswarm[/]      [{C.Muted}]Parallel concurrent dispatch[/]");
                right.AppendLine($"  [{C.Prompt}]/agent[/]       [{C.Muted}]Single-agent conversation[/]");
                right.AppendLine($"  [{C.Prompt}]/stateless[/]   [{C.Muted}]One-off stateless task[/]");
                right.AppendLine($"  [{C.Prompt}]/onboard[/]     [{C.Muted}]Set up your operator profile[/]");
                right.AppendLine($"  [{C.Prompt}]/workflow[/]    [{C.Muted}]Run a workflow file[/]");
                right.AppendLine($"  [{C.Prompt}]/help[/]        [{C.Muted}]Full command reference[/]");
                right.AppendLine();
                right.AppendLine($"[{C.Muted}]────────────────────────────────────────────[/]");
                right.AppendLine($"[{C.Step}]Quick Tips[/]");
                right.AppendLine($"  [{C.Muted}]/qc or /qm to exit an active session[/]");
                right.Append($"  [{C.Muted}]/status to view current config[/]");

                var rightFar = new StringBuilder();
                rightFar.AppendLine($"[{C.Step}]Recent Sessions[/]");
                rightFar.AppendLine($"[{C.Muted}]────────────────────────────────[/]");

                var sessionsPath = PlatformContext.SessionsDirectory;
                if (Directory.Exists(sessionsPath))
                {
                    var recentDirs = Directory.GetDirectories(sessionsPath)
                        .OrderByDescending(d => d)
                        .Take(5)
                        .ToList();

                    if (recentDirs.Count > 0)
                    {
                        foreach (var dir in recentDirs)
                        {
                            string ts = Path.GetFileName(dir);
                            var files = Directory.GetFiles(dir, "*_session.json", SearchOption.AllDirectories);
                            string type = files.Length > 1 ? "swarm" : "agent";

                            string preview = "No preview";
                            if (files.Length > 0)
                            {
                                try
                                {
                                    var raw = Common.GetFirstUserMessage(files[0]);
                                    if (!string.IsNullOrWhiteSpace(raw)
                                        && !raw.StartsWith("[SYSTEM", StringComparison.OrdinalIgnoreCase)
                                        && !raw.StartsWith("[CONTEXT SUMMARY", StringComparison.OrdinalIgnoreCase)
                                        && !raw.StartsWith("## User Context", StringComparison.OrdinalIgnoreCase)
                                        && !raw.Contains("Context restoration", StringComparison.OrdinalIgnoreCase)
                                        && !raw.Contains("Filesystem Write Rules", StringComparison.OrdinalIgnoreCase))
                                    {
                                        preview = raw.Length > 40 ? raw[..40] + "..." : raw;
                                    }
                                }
                                catch { /* ignore */ }
                            }

                            rightFar.AppendLine($"  [{C.Prompt}]{Esc(ts)}[/]");
                            rightFar.AppendLine($"    [{C.Muted}]{type} · {Esc(preview)}[/]");
                        }
                    }
                    else
                    {
                        rightFar.AppendLine($"  [{C.Muted}]No sessions yet[/]");
                    }
                }
                else
                {
                    rightFar.AppendLine($"  [{C.Muted}]No sessions yet[/]");
                }

                rightFar.AppendLine();
                rightFar.AppendLine($"[{C.Muted}]────────────────────────────────[/]");
                rightFar.AppendLine($"  [{C.Muted}]/resume to continue a session[/]");
                rightFar.Append($"  [{C.Muted}]/sessions to view session info[/]");

                var grid = new Grid();
                grid.AddColumn(new GridColumn().NoWrap().PadRight(4));
                grid.AddColumn(new GridColumn().Width(1).NoWrap());
                grid.AddColumn(new GridColumn().PadLeft(3).PadRight(3));
                grid.AddColumn(new GridColumn().Width(1).NoWrap());
                grid.AddColumn(new GridColumn().PadLeft(3));
                grid.AddRow(
                    new Markup(left.ToString()),
                    new Markup($"[{C.Muted}]│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│[/]"),
                    new Markup(right.ToString()),
                    new Markup($"[{C.Muted}]│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│[/]"),
                    new Markup(rightFar.ToString())
                );

                var panel = new Panel(grid)
                    .Border(BoxBorder.Rounded)
                    .BorderStyle(new Style(Color.Grey35))
                    .Padding(1, 1)
                    .Expand();

                AnsiConsole.Write(panel);
            }

            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule().RuleStyle(new Style(Color.Grey23)));
            AnsiConsole.WriteLine();
        });
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

        if (StdioMode)
        {
            EmitJson("input_request", D(("prompt", message), ("default", defaultValue)));
            var input = InputOverride.ReadLine()?.Trim() ?? string.Empty;
            return string.IsNullOrEmpty(input) && defaultValue != null ? defaultValue : input;
        }

        if (InputOverride != Console.In)
        {
            string input = InputOverride.ReadLine()?.Trim() ?? string.Empty;
            return string.IsNullOrEmpty(input) && defaultValue != null ? defaultValue : input;
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
            if (StdioMode) { EmitJson("agent_turn_start", D(("agent", agentName))); }
            else
            {
                AnsiConsole.WriteLine();
                AnsiConsole.Write(new Rule($"[{C.Agent}]{Esc(agentName)}[/]")
                    .RuleStyle(new Style(Color.Grey23))
                    .LeftJustified());
            }
        });

        HookWorker.Enqueue(new HookEvent
        {
            Event = "agent_turn_start",
            Agent = agentName,
            Timestamp = DateTimeOffset.UtcNow
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
            }
            else
            {
                string truncTask = task.Length > truncLength ? task[..truncLength] + "..." : task;
                AnsiConsole.MarkupLine($"  [{C.Accent}]>>[/] [{C.Info}]{Esc(fromAgent)}[/] [{C.Muted}]->[/] [{C.Step}]{Esc(toAgent)}[/]");
                AnsiConsole.MarkupLine($"     [{C.Muted}]{Esc(truncTask)}[/]");
            }
        });

        HookWorker.Enqueue(new HookEvent
        {
            Event = "delegation",
            Agent = fromAgent,
            Summary = task.Length > truncLength ? task[..truncLength] + "..." : task,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    public static void WriteToolCall(string agent, string tool, string? args = null)
    {
        WithConsole(() =>
        {
            if (StdioMode)
            {
                EmitJson("tool_call", D(("agent", agent), ("tool", tool), ("args", args)));
            }
            else
            {
                AnsiConsole.MarkupLine($"  [{C.Muted}]--[/] [{C.Info}]{Esc(agent)}[/] [{C.Muted}]->[/] [{C.Accent}]{Esc(tool)}[/]");
            }
        });

        HookWorker.Enqueue(new HookEvent
        {
            Event = "tool_call",
            Agent = agent,
            Tool = tool,
            Args = args,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    public static void WriteToolResult(string agent, string summary)
    {
        WithConsole(() =>
        {
            if (StdioMode)
            {
                EmitJson("tool_result", D(("agent", agent), ("summary", summary)));
            }
            else
            {
                string clean = CollapseWhitespace(summary);
                if (clean.Length > 120) clean = clean[..120] + "...";
                AnsiConsole.MarkupLine($"     [{C.Muted}]{Esc(clean)}[/]");
            }
        });

        HookWorker.Enqueue(new HookEvent
        {
            Event = "tool_result",
            Agent = agent,
            Summary = summary,
            Timestamp = DateTimeOffset.UtcNow
        });
    }
    
    public static void WriteToolResult(string agent, string tool, string fullResult)
    {
        if (StdioMode)
            EmitJson("tool_result", D(("agent", agent), ("tool", tool), ("result", fullResult)));
    }

    public static void WriteTaskComplete(string agent, string summary)
    {
        WithConsole(() =>
        {
            if (StdioMode)
            {
                EmitJson("task_complete", D(("agent", agent), ("summary", summary)));
            }
            else
            {
                AnsiConsole.MarkupLine($"  [{C.Success}]✓[/] [{C.Step}]{Esc(agent)}[/] [{C.Muted}]completed[/]  [{C.Info}]{Esc(summary)}[/]");
            }
        });

        HookWorker.Enqueue(new HookEvent
        {
            Event = "task_complete",
            Agent = agent,
            Summary = summary,
            Timestamp = DateTimeOffset.UtcNow
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

    public static void PrintHelp(string helpText)
    {
        if (StdioMode)
        {
            WriteBody(helpText);
            return;
        }

        if (AnsiConsole.Profile.Width < 140)
        {
            WritePanel("Mux-Swarm — Command Reference", helpText);
            return;
        }

        var commands = new StringBuilder();
        commands.AppendLine($"[{C.Step}]Modes[/]");
        commands.AppendLine($"[{C.Muted}]────────────────────────────────────[/]");
        commands.AppendLine($"  [{C.Prompt}]/swarm[/]         [{C.Muted}]Multi-agent orchestrated loop[/]");
        commands.AppendLine($"  [{C.Prompt}]/pswarm[/]        [{C.Muted}]Parallel concurrent dispatch[/]");
        commands.AppendLine($"  [{C.Prompt}]/agent[/]         [{C.Muted}]Single-agent conversation[/]");
        commands.AppendLine($"  [{C.Prompt}]/stateless[/]     [{C.Muted}]One-off stateless task[/]");
        commands.AppendLine($"  [{C.Prompt}]/workflow[/]      [{C.Muted}]Run a workflow file[/]");
        commands.AppendLine($"  [{C.Prompt}]/resume[/]        [{C.Muted}]Resume a previous session[/]");
        commands.AppendLine();
        commands.AppendLine($"[{C.Step}]Execution[/]");
        commands.AppendLine($"[{C.Muted}]────────────────────────────────────[/]");
        commands.AppendLine($"  [{C.Prompt}]/plan[/]          [{C.Muted}]Toggle plan mode (confirm before exec)[/]");
        commands.AppendLine($"  [{C.Prompt}]/continuous[/]    [{C.Muted}]Toggle autonomous execution[/]");
        commands.AppendLine();
        commands.AppendLine($"[{C.Step}]Session[/]");
        commands.AppendLine($"[{C.Muted}]────────────────────────────────────[/]");
        commands.AppendLine($"  [{C.Prompt}]/compact[/]       [{C.Muted}]Compress context (single agent)[/]");
        commands.AppendLine($"  [{C.Prompt}]/sessions[/]      [{C.Muted}]List saved sessions[/]");
        commands.AppendLine($"  [{C.Prompt}]/report[/]        [{C.Muted}]Generate session audit report[/]");
        commands.AppendLine($"  [{C.Prompt}]/report <id>[/]   [{C.Muted}]Audit a specific session[/]");
        commands.AppendLine($"  [{C.Prompt}]/clear[/]         [{C.Muted}]Clear screen[/]");
        commands.AppendLine($"  [{C.Prompt}]/qc[/] [{C.Muted}]/[/] [{C.Prompt}]/qm[/]      [{C.Muted}]Exit active session[/]");
        commands.Append($"  [{C.Prompt}]/exit[/]          [{C.Muted}]Exit Mux-Swarm[/]");

        var config = new StringBuilder();
        config.AppendLine($"[{C.Step}]Configuration[/]");
        config.AppendLine($"[{C.Muted}]────────────────────────────────────[/]");
        config.AppendLine($"  [{C.Prompt}]/model[/]         [{C.Muted}]View current models[/]");
        config.AppendLine($"  [{C.Prompt}]/setmodel[/]      [{C.Muted}]Change agent/orchestrator model[/]");
        config.AppendLine($"  [{C.Prompt}]/swap[/]          [{C.Muted}]Swap active single agent[/]");
        config.AppendLine($"  [{C.Prompt}]/provider[/]      [{C.Muted}]View or switch LLM provider[/]");
        config.AppendLine($"  [{C.Prompt}]/limits[/]        [{C.Muted}]View execution limits[/]");
        config.AppendLine($"  [{C.Prompt}]/tools[/]         [{C.Muted}]List MCP tools[/]");
        config.AppendLine($"  [{C.Prompt}]/skills[/]        [{C.Muted}]List local skills[/]");
        config.AppendLine($"  [{C.Prompt}]/memory[/]        [{C.Muted}]View knowledge graph[/]");
        config.AppendLine($"  [{C.Prompt}]/status[/]        [{C.Muted}]Full system status[/]");
        config.AppendLine($"  [{C.Prompt}]/setup[/]         [{C.Muted}]Reconfigure[/]");
        config.AppendLine($"  [{C.Prompt}]/onboard[/]       [{C.Muted}]Create or update BRAIN.md and MEMORY.md[/]");
        config.AppendLine($"  [{C.Prompt}]/refresh[/]       [{C.Muted}]Reload config, MCPs, skills[/]");
        config.AppendLine($"  [{C.Prompt}]/reloadskills[/]  [{C.Muted}]Refresh skills only[/]");
        config.AppendLine($"  [{C.Prompt}]/dockerexec[/]    [{C.Muted}]Toggle Docker exec mode[/]");
        config.AppendLine($"  [{C.Prompt}]/delimiter[/]     [{C.Muted}]Toggle multi-line input[/]");
        config.AppendLine($"  [{C.Prompt}]/dbg[/]           [{C.Muted}]Enable tool call output (stdio)[/]");
        config.Append($"  [{C.Prompt}]/nodbg[/]         [{C.Muted}]Disable tool call output (stdio)[/]");

        var cli = new StringBuilder();
        cli.AppendLine($"[{C.Step}]CLI Usage[/]");
        cli.AppendLine($"[{C.Muted}]────────────────────────────────────────[/]");
        cli.AppendLine($"  [{C.Prompt}]ms \"<goal>\"[/]");
        cli.AppendLine($"  [{C.Prompt}]ms <goal.txt>[/]");
        cli.AppendLine($"  [{C.Prompt}]ms --goal \"<goal>\"[/]");
        cli.AppendLine($"  [{C.Prompt}]ms --goal <goal.txt>[/]");
        cli.AppendLine($"  [{C.Prompt}]ms --continuous --goal \"<goal>\"[/]");
        cli.AppendLine($"  [{C.Prompt}]ms --parallel --goal \"<goal>\"[/]");
        cli.AppendLine();
        cli.AppendLine($"[{C.Step}]Flags[/]");
        cli.AppendLine($"[{C.Muted}]────────────────────────────────────────[/]");
        cli.AppendLine($"  [{C.Prompt}]--goal <text|file>[/]       [{C.Muted}]Explicit goal[/]");
        cli.AppendLine($"  [{C.Prompt}]--continuous[/]             [{C.Muted}]Autonomous loop mode[/]");
        cli.AppendLine($"  [{C.Prompt}]--parallel[/]               [{C.Muted}]Concurrent batch dispatch[/]");
        cli.AppendLine($"  [{C.Prompt}]--max-parallelism <n>[/]    [{C.Muted}]Max concurrent tasks (default 4)[/]");
        cli.AppendLine($"  [{C.Prompt}]--agent <name>[/]           [{C.Muted}]Single agent mode[/]");
        cli.AppendLine($"  [{C.Prompt}]--plan[/]                   [{C.Muted}]Confirm before executing[/]");
        cli.AppendLine($"  [{C.Prompt}]--workflow <file>[/]        [{C.Muted}]Run workflow file[/]");
        cli.AppendLine($"  [{C.Prompt}]--provider <name>[/]        [{C.Muted}]Set LLM provider[/]");
        cli.AppendLine($"  [{C.Prompt}]--goal-id <id>[/]           [{C.Muted}]Session identifier[/]");
        cli.AppendLine($"  [{C.Prompt}]--min-delay <secs>[/]       [{C.Muted}]Loop delay (default 300)[/]");
        cli.AppendLine($"  [{C.Prompt}]--persist-interval <s>[/]   [{C.Muted}]Save interval in seconds[/]");
        cli.AppendLine($"  [{C.Prompt}]--session-retention <n>[/]  [{C.Muted}]Keep last N sessions[/]");
        cli.AppendLine($"  [{C.Prompt}]--stdio[/]                  [{C.Muted}]Machine-readable NDJSON[/]");
        cli.AppendLine($"  [{C.Prompt}]--delimiter <str>[/]        [{C.Muted}]Multi-line input delimiter[/]");
        cli.AppendLine($"  [{C.Prompt}]--serve <port>[/]           [{C.Muted}]Start embedded web UI[/]");
        cli.AppendLine($"  [{C.Prompt}]--daemon[/]                 [{C.Muted}]Start daemon mode[/]");
        cli.AppendLine($"  [{C.Prompt}]--register[/]               [{C.Muted}]Register as OS service[/]");
        cli.AppendLine($"  [{C.Prompt}]--remove[/]                 [{C.Muted}]Unregister OS service[/]");
        cli.AppendLine($"  [{C.Prompt}]--watchdog[/]               [{C.Muted}]External watchdog toggle[/]");
        cli.AppendLine($"  [{C.Prompt}]--mcp-strict[/]             [{C.Muted}]Require all MCPs (default true)[/]");
        cli.AppendLine($"  [{C.Prompt}]--docker-exec[/]            [{C.Muted}]Route exec via Docker[/]");
        cli.AppendLine($"  [{C.Prompt}]--cfg <path>[/]             [{C.Muted}]Override config.json[/]");
        cli.AppendLine($"  [{C.Prompt}]--swarmcfg <path>[/]        [{C.Muted}]Override swarm.json[/]");
        cli.Append($"  [{C.Prompt}]--report [[id]][/]            [{C.Muted}]Generate report(s) and exit[/]");

        var grid = new Grid();
        grid.AddColumn(new GridColumn().PadRight(3));
        grid.AddColumn(new GridColumn().Width(1).NoWrap());
        grid.AddColumn(new GridColumn().PadLeft(3).PadRight(3));
        grid.AddColumn(new GridColumn().Width(1).NoWrap());
        grid.AddColumn(new GridColumn().PadLeft(3));
        grid.AddRow(
            new Markup(commands.ToString()),
            new Markup($"[{C.Muted}]│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│[/]"),
            new Markup(config.ToString()),
            new Markup($"[{C.Muted}]│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│\n│[/]"),
            new Markup(cli.ToString())
        );

        var panel = new Panel(grid)
            .Header($"[{C.Step}]Mux-Swarm — Command Reference[/]")
            .Border(BoxBorder.Rounded)
            .BorderStyle(new Style(Color.Grey35))
            .Padding(1, 1)
            .Expand();

        AnsiConsole.Write(panel);
    }
}