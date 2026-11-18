using System.Text.Json;

namespace MuxSwarm.Engine.Extensions;

/// <summary>
/// Slash commands served by extension processes. <c>/name args</c> sends a <c>command/run</c>
/// request to the owning process; while it runs, <c>command/output</c> notifications are shown live
/// and Esc cancels (Mux sends <c>$/cancelRequest</c>). The reply may carry <c>output</c> (shown to the
/// user) and <c>prompt</c> (sent to the agent as if the user typed it). Built-in names are never taken.
/// </summary>
internal static class ExtensionCommands
{
    /// <summary>What a command run produced.</summary>
    internal readonly record struct Outcome(bool Handled, string? Prompt);

    private static volatile ExtensionRegistry.ExtensionCommand[] _commands = [];
    private static volatile (string Cmd, string Desc)[] _entries = [];

    /// <summary>The loaded commands as palette entries (<c>/name</c>, description). Swapped as a whole on reload.</summary>
    internal static (string Cmd, string Desc)[] Entries => _entries;

    /// <summary>Publish the commands of the loaded extensions (called by the extension loader).</summary>
    internal static void Set(IEnumerable<ExtensionRegistry.ExtensionCommand> commands)
    {
        var all = commands.ToArray();
        _commands = all;
        _entries = all.Select(c => (c.Cmd, $"{c.Description} [{c.Extension}]".TrimStart())).ToArray();
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
                new { name = cmd.Name, args, cwd = PlatformContext.WorkspaceRoot, mode = ServeMode.ActiveMode },
                TimeSpan.FromSeconds(cmd.TimeoutSeconds), cts.Token);
        }
        finally
        {
            proc.CommandOutput = null;
            StdinCancelMonitor.Instance?.ClearActiveTurnCts();
        }
        return Interpret(cmd, reply, cancelled: cts.IsCancellationRequested, running: proc.IsRunning);
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
        string? prompt = result.TryGetProperty("prompt", out var p) && p.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(p.GetString()) ? p.GetString() : null;
        return new(true, prompt);
    }
}
