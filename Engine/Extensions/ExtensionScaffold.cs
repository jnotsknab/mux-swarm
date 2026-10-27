using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MuxSwarm.Engine.Extensions;

/// <summary>
/// Creating and opening extensions for the <c>/extensions</c> panel: <see cref="Scaffold"/> writes a
/// new extension folder from the parts the user picked, <see cref="Open"/> opens a manifest or
/// folder in the user's editor or the OS default app.
/// </summary>
internal static class ExtensionScaffold
{
    /// <summary>Parts the <c>/extensions new</c> scaffold can include.</summary>
    [Flags]
    internal enum Parts { None = 0, Theme = 1, Cosmetics = 2, FooterChip = 4, Command = 8, Tool = 16, Intercept = 32, Panel = 64 }

    /// <summary>Parts that need a process (and trust) to work.</summary>
    internal const Parts ProcessParts = Parts.FooterChip | Parts.Command | Parts.Tool | Parts.Intercept | Parts.Panel;

    /// <summary>
    /// Write a new extension folder <c>root/name</c>: an <c>extension.json</c> with the chosen
    /// <paramref name="parts"/>, plus a working <c>main.py</c> when a part needs a process.
    /// Returns the folder, or null with <paramref name="error"/> (bad name, folder exists).
    /// </summary>
    internal static string? Scaffold(string root, string name, string? description, Parts parts, out string error)
    {
        name = (name ?? "").Trim().ToLowerInvariant();
        error = "";
        if (!ExtensionRegistry.IsValidName(name)) { error = $"'{name}' is not a valid name (a-z, 0-9 and '-', max 32 chars)."; return null; }
        var dir = Path.Combine(root, name);
        if (Directory.Exists(dir)) { error = $"{dir} already exists."; return null; }

        var m = new JsonObject { ["name"] = name, ["version"] = "0.1.0", ["description"] = string.IsNullOrWhiteSpace(description) ? $"The {name} extension" : description.Trim() };
        if (parts.HasFlag(Parts.Theme))
            m["themes"] = new JsonArray(new JsonObject { ["name"] = name, ["base"] = "dark", ["accent"] = "#7aa2f7", ["success"] = "#9ece6a" });
        if (parts.HasFlag(Parts.Cosmetics))
        {
            m["spinner"] = new JsonArray("\u25d0", "\u25d3", "\u25d1", "\u25d2");
            m["quips"] = new JsonArray("Tinkering", "Wiring things up");
        }
        if ((parts & ProcessParts) != 0)
        {
            m["process"] = new JsonObject { ["command"] = PlatformContext.IsWindows ? "python" : "python3", ["args"] = new JsonArray("-u", "main.py") };
            m["events"] = new JsonArray("turn_end");
        }
        var commands = new JsonArray();
        if (parts.HasFlag(Parts.Command))
            commands.Add(new JsonObject
            {
                ["name"] = CommandName(name), ["description"] = $"Say hello from {name}", ["usage"] = "[who]",
                ["subcommands"] = new JsonArray(new JsonObject { ["name"] = "loud", ["description"] = "Say it louder" }),
            });
        if (parts.HasFlag(Parts.Panel))   // main.py answers this one with a panel ("open" in the reply)
            commands.Add(new JsonObject { ["name"] = PanelCommand(name), ["description"] = $"Open the {name} panel", ["immediate"] = true });
        if (commands.Count > 0) m["commands"] = commands;
        if (parts.HasFlag(Parts.Tool))
            m["tools"] = new JsonArray(new JsonObject
            {
                ["name"] = "hello", ["description"] = $"Say hello from the {name} extension.", ["agents"] = new JsonArray("*"),
                ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["who"] = new JsonObject { ["type"] = "string" } } },
            });
        if (parts.HasFlag(Parts.Intercept))
            m["intercepts"] = new JsonArray(new JsonObject { ["tool"] = "execute_command_async", ["on"] = "before" });

        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ExtensionRegistry.ManifestFileName), m.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        if ((parts & ProcessParts) != 0) File.WriteAllText(Path.Combine(dir, "main.py"), MainPy);
        return dir;
    }


    // "<command>-panel", cut to the 32-char command limit.
    private static string PanelCommand(string name)
    {
        var cmd = CommandName(name) + "-panel";
        return cmd.Length <= 32 ? cmd : cmd[..32];
    }

    // The extension name when it is a usable command word, else "ext-<name>".
    private static string CommandName(string name)
    {
        if (char.IsLetter(name[0]) && !ExtensionRegistry.IsBuiltInCommand("/" + name)) return name;
        var cmd = "ext-" + name;
        return cmd.Length <= 32 ? cmd : cmd[..32];
    }

    private const string MainPy = """
        # Mux extension process. Reference: the '## Extensions' and '## Event Hooks' sections of Mux's Context/DOCS.md.
        # Protocol: newline-delimited JSON-RPC 2.0. Mux -> us on stdin; us -> Mux on stdout.
        # stdout carries ONLY JSON lines; log to stderr (/extensions shows the last lines).
        import json, os, sys

        sys.stdout.reconfigure(encoding="utf-8")   # Windows pipes default to the ANSI code page
        name, data_dir = "extension", "."           # from initialize; write files under data_dir


        def send(obj):
            print(json.dumps(obj), flush=True)


        def notify(method, params):
            send({"jsonrpc": "2.0", "method": method, "params": params})


        def reply(msg_id, result):
            send({"jsonrpc": "2.0", "id": msg_id, "result": result})


        def error(msg_id, message):
            send({"jsonrpc": "2.0", "id": msg_id, "error": {"code": 1, "message": message}})


        def log(text):
            print(text, file=sys.stderr, flush=True)


        turns = 0
        cancelled = set()   # request ids Mux cancelled ($/cancelRequest: the user pressed Esc)
        clicks = 0


        def panel_view():
            # Element tree Mux draws (TUI alt-screen view, web drawer). See "### Extension panels" in DOCS.md.
            return {"type": "box", "gap": 1, "children": [
                {"type": "text", "text": f"{turns} turns, {clicks} clicks", "bold": True, "color": "accent"},
                {"type": "input", "key": "say", "placeholder": "type and press Enter"},
                {"type": "button", "key": "click", "label": "Click me", "hotkey": "k"},
            ]}

        for line in sys.stdin:
            msg = json.loads(line)
            method, params = msg.get("method"), msg.get("params") or {}

            # --- notifications (no id, no reply) ---
            if "id" not in msg:
                if method == "initialize":            # { protocolVersion, muxVersion, name, dir, dataDir? }
                    name = params.get("name", name)
                    data_dir = params.get("dataDir") or params.get("dir") or "."
                    notify("footer/set", {"id": "status", "text": "ready"})   # chip id -> "<name>.status"
                elif method == "event":               # lifecycle event, same payload as hooks
                    if params.get("event") == "turn_end":
                        turns += 1
                        notify("footer/set", {"id": "status", "text": f"{turns} turns"})
                elif method == "$/cancelRequest":
                    cancelled.add(params.get("id"))
                elif method == "ui/event":            # { id, key, type: press|submit|select|close, value }
                    if params.get("type") == "press":
                        clicks += 1
                    elif params.get("type") == "submit":
                        send({"jsonrpc": "2.0", "id": "toast", "method": "ui/toast", "params": {"text": f"you said {params.get('value')!r}"}})
                    if params.get("type") != "close":
                        send({"jsonrpc": "2.0", "id": "upd", "method": "ui/update", "params": {"id": "main", "view": panel_view()}})
                continue

            # --- replies to our own requests (an id but no method) ---
            if method is None:
                continue


            # --- requests (reply with the same id) ---
            msg_id = msg["id"]
            if method == "command/run" and params.get("name", "").endswith("-panel"):
                reply(msg_id, {"open": {"id": "main", "title": name, "view": panel_view()}})   # = ui/open
            elif method == "command/run":             # { name, args, cwd, mode }
                notify("command/output", {"text": "working..."})        # streamed progress line
                reply(msg_id, {"output": f"hello from {name} (args: {params.get('args')!r})"})
                # add "prompt": "..." to the result to send a message to the agent as the next turn
                # requests TO Mux (any time): send({"jsonrpc": "2.0", "id": "t1", "method": "ui/toast", "params": {"text": "done"}})
                # (also session/info, ui/log, prompt/submit, command/run; replies arrive on stdin with that id, no "method")
            elif method == "tool/call":               # { name, arguments } -> { content }
                who = (params.get("arguments") or {}).get("who", "there")
                reply(msg_id, {"content": f"hello {who}"})
            elif method == "tool/before":             # { tool, arguments } -> allow | deny (+ reason) | rewritten arguments
                reply(msg_id, {"decision": "allow"})
            elif method == "tool/after":              # { tool, arguments, result } -> {} keeps it, { result } replaces it
                reply(msg_id, {})
            else:
                error(msg_id, f"unknown method {method}")

        """;

    /// <summary>The user's terminal editor from <c>$VISUAL</c> or <c>$EDITOR</c>, or null when neither is set.</summary>
    internal static string? TerminalEditor()
    {
        var e = Environment.GetEnvironmentVariable("VISUAL") ?? Environment.GetEnvironmentVariable("EDITOR");
        return string.IsNullOrWhiteSpace(e) ? null : e;
    }

    /// <summary>
    /// Open <paramref name="path"/> for the user. With <paramref name="edit"/> and <c>$VISUAL</c> /
    /// <c>$EDITOR</c> set, runs that editor in this console and waits (returns true = edited);
    /// otherwise hands the path to the OS default app without waiting (returns false). Throws when
    /// nothing can open it.
    /// </summary>
    internal static bool Open(string path, bool edit)
    {
        var editor = edit ? TerminalEditor() : null;
        if (editor is not null)
        {
            // The editor string is the user's own (it may carry flags, e.g. "code -w"), so a shell runs it.
            // cmd /s /c strips the outer quotes and runs: editor "path" (Windows paths cannot contain quotes).
            var psi = PlatformContext.IsWindows
                ? new ProcessStartInfo("cmd.exe") { Arguments = $"/s /c \"{editor} \"{path}\"\"" }
                : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", editor + " \"$1\"", "sh", path } };
            psi.UseShellExecute = false;
            using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start '{editor}'.");
            p.WaitForExit();
            return true;
        }
        if (PlatformContext.IsWindows)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
            catch (System.ComponentModel.Win32Exception) when (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { path }, UseShellExecute = false })?.Dispose();   // no app registered for .json
            }
            return false;
        }
        var open = new ProcessStartInfo(PlatformContext.IsMac ? "open" : "xdg-open")
        {
            ArgumentList = { path }, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using (var p = Process.Start(open) ?? throw new InvalidOperationException("could not start the system opener."))
            p.StandardInput.Close();
        return false;
    }
}
