using System.Text.Json;

namespace MuxSwarm.Engine.Extensions;

/// <summary>
/// Slash commands served by extension processes. <c>/name args</c> sends a <c>command/run</c>
/// request to the owning process; while it runs, <c>command/output</c> notifications are shown live
/// and Esc cancels (Mux sends <c>$/cancelRequest</c>). The reply may carry <c>output</c> (shown to the
/// user), <c>prompt</c> (sent to the agent as if the user typed it) and <c>open</c> (a panel, as
/// <c>ui/open</c>). Built-in names are never taken. <c>immediate</c> commands also run mid-turn.
/// </summary>
internal static class ExtensionCommands
{
    /// <summary>What a command run produced.</summary>
    internal readonly record struct Outcome(bool Handled, string? Prompt);

    private static volatile ExtensionRegistry.ExtensionCommand[] _commands = [];
    private static volatile (string Cmd, string Desc)[] _entries = [];

    /// <summary>
    /// The loaded commands as palette entries (<c>/name</c>, description + usage hint), each followed by one
    /// row per subcommand (<c>/name sub</c>). Swapped as a whole on reload.
    /// </summary>
    internal static (string Cmd, string Desc)[] Entries => _entries;

    /// <summary>Publish the commands of the loaded extensions (called by the extension loader).</summary>
    internal static void Set(IEnumerable<ExtensionRegistry.ExtensionCommand> commands)
    {
        var all = commands.ToArray();
        _commands = all;
        _entries = all.SelectMany(PaletteRows).ToArray();
    }

    /// <summary>
    /// Palette rows for <paramref name="c"/>: <c>/name</c> with <c>"description  usage [ext]"</c>, then
    /// <c>/name sub</c> with the subcommand's description, the same shape as built-in subcommand rows.
    /// </summary>
    internal static IEnumerable<(string Cmd, string Desc)> PaletteRows(ExtensionRegistry.ExtensionCommand c)
    {
        var head = string.Join("  ", new[] { c.Description, c.Usage }.Where(s => s.Length > 0));
        yield return (c.Cmd, $"{head} [{c.Extension}]".TrimStart());
        foreach (var (sub, desc) in c.Subcommands)
            yield return ($"{c.Cmd} {sub}", desc.Length > 0 ? desc : $"[{c.Extension}]");
    }

    /// <summary>The command <paramref name="line"/> invokes (its first word), or null.</summary>
    internal static ExtensionRegistry.ExtensionCommand? Match(string? line)
    {
        var t = (line ?? "").Trim();
        if (t.Length < 2 || t[0] != '/') return null;
        var word = t.Split(' ', 2)[0];
        return _commands.FirstOrDefault(c => string.Equals(c.Cmd, word, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Run the extension command in <paramref name="line"/>. Not handled when the line is not an
    /// extension command. Never throws for extension failures: they are reported and the command
    /// counts as handled. Esc (or the web/stdio cancel) stops the wait.
    /// </summary>
    internal static async Task<Outcome> RunAsync(string? line, CancellationToken ct)
    {
        if (Match(line) is not { } cmd) return new(false, null);
        var proc = ExtensionHost.Running.FirstOrDefault(p => p.Name == cmd.Extension);
        if (proc is not { IsRunning: true })
        {
            MuxConsole.WriteWarning($"{cmd.Cmd}: extension '{cmd.Extension}' is not running (see /extensions).");
            return new(true, null);
        }
        var trimmed = line!.Trim();
        string args = trimmed.Length > cmd.Cmd.Length ? trimmed[cmd.Cmd.Length..].Trim() : "";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var esc = EscapeKeyListener.Start(cts, ct);
        StdinCancelMonitor.Instance?.SetActiveTurnCts(cts);
        proc.CommandOutput = text => MuxConsole.WriteBody(text);
        JsonElement reply;
        try
        {
            reply = await proc.RequestAsync("command/run",
                new { name = cmd.Name, args, cwd = PlatformContext.WorkspaceRoot, mode = ServeMode.ActiveMode, agent = ExtensionHostApi.SessionAgent() },
                TimeSpan.FromSeconds(cmd.TimeoutSeconds), cts.Token);
        }
        finally
        {
            proc.CommandOutput = null;
            StdinCancelMonitor.Instance?.ClearActiveTurnCts();
        }
        return Interpret(cmd, reply, cancelled: cts.IsCancellationRequested, running: proc.IsRunning);
    }

    /// <summary>
    /// If <paramref name="line"/> invokes an <c>immediate</c> command, start it in the background and
    /// return true (the caller drops the line instead of queuing it). Its output shows as usual, a
    /// returned prompt is queued as the next input, and Esc never waits on it (only its timeout ends it).
    /// </summary>
    internal static bool TryRunImmediate(string? line)
    {
        if (Match(line) is not { Immediate: true } cmd) return false;
        _ = Task.Run(async () =>
        {
            var proc = ExtensionHost.Running.FirstOrDefault(p => p.Name == cmd.Extension);
            if (proc is not { IsRunning: true })
            {
                MuxConsole.WriteWarning($"{cmd.Cmd}: extension '{cmd.Extension}' is not running (see /extensions).");
                return;
            }
            var trimmed = line!.Trim();
            string args = trimmed.Length > cmd.Cmd.Length ? trimmed[cmd.Cmd.Length..].Trim() : "";
            var reply = await proc.RequestAsync("command/run",
                new { name = cmd.Name, args, cwd = PlatformContext.WorkspaceRoot, mode = ServeMode.ActiveMode, immediate = true, agent = ExtensionHostApi.SessionAgent() },
                TimeSpan.FromSeconds(cmd.TimeoutSeconds));
            if (Interpret(cmd, reply, cancelled: false, running: proc.IsRunning).Prompt is { } prompt
                && ExtensionHostApi.Inject(cmd.Extension, prompt) is { } why)
                MuxConsole.WriteWarning($"{cmd.Cmd}: prompt not sent ({why}).");
        });
        return true;
    }

    /// <summary>Turn a <c>command/run</c> response into output + an optional prompt. Pure, for tests.</summary>
    internal static Outcome Interpret(ExtensionRegistry.ExtensionCommand cmd, JsonElement reply, bool cancelled, bool running)
    {
        if (reply.ValueKind != JsonValueKind.Object)
        {
            MuxConsole.WriteWarning($"{cmd.Cmd}: {(cancelled ? "cancelled" : !running ? "the extension exited" : $"no reply within {cmd.TimeoutSeconds}s")}.");
            return new(true, null);
        }
        if (reply.TryGetProperty("error", out var error))
        {
            string message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()! : error.ToString();
            MuxConsole.WriteWarning($"{cmd.Cmd} failed: {message}");
            return new(true, null);
        }
        if (!reply.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) return new(true, null);
        if (result.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.String && output.GetString() is { Length: > 0 } text)
            MuxConsole.WritePanel(cmd.Cmd, text);
        // "open": { id, title?, view } = ui/open in the same reply (saves a round trip).
        if (result.TryGetProperty("open", out var open) && open.ValueKind == JsonValueKind.Object)
        {
            string? Str(string k) => open.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var view = open.TryGetProperty("view", out var v) ? v : default;
            int? Int(string k) => open.TryGetProperty(k, out var n) && n.TryGetInt32(out var i) ? i : null;
            if (ExtensionPanels.Open(cmd.Extension, Str("id") ?? "", Str("title"), view, Str("placement"), Int("columns"), Int("rows")) is { } err)
                MuxConsole.WriteWarning($"{cmd.Cmd}: panel not opened: {err}");
        }
        string? prompt = result.TryGetProperty("prompt", out var p) && p.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(p.GetString()) ? p.GetString() : null;
        return new(true, prompt);
    }
}
