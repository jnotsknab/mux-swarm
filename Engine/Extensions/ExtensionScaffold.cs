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
    internal enum Parts { None = 0, Theme = 1, Cosmetics = 2, FooterChip = 4, Command = 8, Tool = 16, Intercept = 32 }

    /// <summary>Parts that need a process (and trust) to work.</summary>
    internal const Parts ProcessParts = Parts.FooterChip | Parts.Command | Parts.Tool | Parts.Intercept;

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
        if (parts.HasFlag(Parts.Command))
            m["commands"] = new JsonArray(new JsonObject { ["name"] = CommandName(name), ["description"] = $"Say hello from {name}" });
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

    // The extension name when it is a usable command word, else "ext-<name>".
    private static string CommandName(string name)
    {
        if (char.IsLetter(name[0]) && !ExtensionRegistry.IsBuiltInCommand("/" + name)) return name;
        var cmd = "ext-" + name;
        return cmd.Length <= 32 ? cmd : cmd[..32];
    }

    private const string MainPy = """
        # Mux extension process: newline-delimited JSON-RPC 2.0 on stdin/stdout (see DOCS.md, ## Extensions).
        # Never print anything else to stdout; use stderr for logs (/extensions shows the last lines).
        import json, sys

        name, data_dir = "extension", "."   # data_dir: where to write files (initialize.dataDir when sandboxed)


        def send(obj):
            print(json.dumps(obj), flush=True)


        def reply(msg_id, result):
            send({"jsonrpc": "2.0", "id": msg_id, "result": result})


        turns = 0
        for line in sys.stdin:
            msg = json.loads(line)
            method, params = msg.get("method"), msg.get("params") or {}
            if "id" not in msg:   # notifications from Mux
                if method == "initialize":
                    name, data_dir = params.get("name", name), params.get("dataDir") or "."
                    send({"jsonrpc": "2.0", "method": "footer/set", "params": {"id": "status", "text": "ready"}})
                elif method == "event" and params.get("event") == "turn_end":
                    turns += 1
                    send({"jsonrpc": "2.0", "method": "footer/set", "params": {"id": "status", "text": f"{turns} turns"}})
                continue
            if method == "command/run":
                reply(msg["id"], {"output": f"hello from {name} (args: {params.get('args')!r})"})
            elif method == "tool/call":
                reply(msg["id"], {"content": f"hello {params.get('arguments', {}).get('who', 'there')}"})
            elif method == "tool/before":
                reply(msg["id"], {"decision": "allow"})
            else:
                send({"jsonrpc": "2.0", "id": msg["id"], "error": {"code": 1, "message": f"unknown method {method}"}})

        """;

    /// <summary>
    /// Open <paramref name="path"/> for the user. With <paramref name="edit"/> and <c>$VISUAL</c> /
    /// <c>$EDITOR</c> set, runs that editor in this console and waits (returns true = edited);
    /// otherwise hands the path to the OS default app without waiting (returns false). Throws when
    /// nothing can open it.
    /// </summary>
    internal static bool Open(string path, bool edit)
    {
        var editor = edit ? Environment.GetEnvironmentVariable("VISUAL") ?? Environment.GetEnvironmentVariable("EDITOR") : null;
        if (!string.IsNullOrWhiteSpace(editor))
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
