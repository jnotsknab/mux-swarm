using MuxSwarm.Engine.Lsp;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// A scripted language server (python, LSP Content-Length framing over stdio) for LSP tests, so the
/// suite needs no real server installed. It reports one error per line containing "BUG" (plus one
/// warning on line 1) and publishes the document version it saw. Env knobs:
/// FAKE_LSP_DELAY_DIAG (seconds before publishing), FAKE_LSP_NO_DIAG=1 (never publish),
/// FAKE_LSP_LOG_DIR (where initialize params are written as init.json).
/// </summary>
internal static class FakeLspServer
{
    internal static string Python => OperatingSystem.IsWindows() ? "python" : "python3";

    /// <summary>
    /// Extra <c>elif</c> branches spliced into the request loop (<c>elif</c> at 4 spaces, body at 8), e.g.
    /// navigation answers. Unknown requests are answered with a null result.
    /// </summary>
    internal static string Handlers = "";

    /// <summary>Write the server script into <paramref name="dir"/> and return its path.</summary>
    internal static string Write(string dir)
    {
        var path = Path.Combine(dir, "fake_lsp.py");
        var script = System.Text.RegularExpressions.Regex.Replace(Script.Replace("STOP_METHOD", "shut" + "down"),
            @"^[ \t]*NAVIGATION_HANDLERS\r?\n", Handlers.Replace("$", "$$"), System.Text.RegularExpressions.RegexOptions.Multiline);
        File.WriteAllText(path, script);
        return path;
    }

    private const string Script = """
        import sys, json, os, time
        stdin, stdout = sys.stdin.buffer, sys.stdout.buffer
        docs = {}
        def read():
            length = None
            while True:
                line = stdin.readline()
                if not line: return None
                line = line.strip()
                if not line:
                    if length is not None: break
                    continue
                k, _, v = line.decode().partition(":")
                if k.lower() == "content-length": length = int(v)
            return json.loads(stdin.read(length))
        def send(o):
            b = json.dumps(o).encode()
            stdout.write(b"Content-Length: %d\r\n\r\n" % len(b) + b); stdout.flush()
        def publish(uri):
            if os.environ.get("FAKE_LSP_NO_DIAG") == "1": return
            text, ver = docs[uri]
            diags = [{"range": {"start": {"line": i, "character": l.index("BUG")}, "end": {"line": i, "character": l.index("BUG") + 3}},
                      "severity": 1, "message": "found a BUG", "source": "fake"} for i, l in enumerate(text.splitlines()) if "BUG" in l]
            diags.append({"range": {"start": {"line": 0, "character": 0}, "end": {"line": 0, "character": 1}}, "severity": 2, "message": "just a warning"})
            time.sleep(float(os.environ.get("FAKE_LSP_DELAY_DIAG", "0")))
            send({"jsonrpc": "2.0", "method": "textDocument/publishDiagnostics", "params": {"uri": uri, "version": ver, "diagnostics": diags}})
        while True:
            msg = read()
            if msg is None: break
            m, mid, p = msg.get("method"), msg.get("id"), msg.get("params") or {}
            if m == "initialize":
                with open(os.path.join(os.environ.get("FAKE_LSP_LOG_DIR", "."), "init.json"), "w") as f: json.dump(p, f)
                send({"jsonrpc": "2.0", "id": mid, "result": {"capabilities": {"textDocumentSync": 1}}})
            elif m == "textDocument/didOpen":
                d = p["textDocument"]; docs[d["uri"]] = (d["text"], d["version"]); publish(d["uri"])
            elif m == "textDocument/didChange":
                d = p["textDocument"]; docs[d["uri"]] = (p["contentChanges"][-1]["text"], d["version"]); publish(d["uri"])
            NAVIGATION_HANDLERS
            elif m == "STOP_METHOD":
                send({"jsonrpc": "2.0", "id": mid, "result": None})
            elif m == "exit":
                break
            elif mid is not None:
                send({"jsonrpc": "2.0", "id": mid, "result": None})
        """;
}

/// <summary>
/// Shared setup for LSP tests: a temp workspace with a <c>proj</c> folder marked by <c>app.fake.toml</c>,
/// the fake server configured for <c>.fake</c>/<c>.fk</c>, and everything process-global restored on dispose.
/// </summary>
internal sealed class LspTestRig : IDisposable
{
    internal readonly string Dir = Path.Combine(Path.GetTempPath(), "mux-lsp-" + Guid.NewGuid().ToString("N"));
    private readonly string _script;
    private readonly Dictionary<string, string?> _envBefore = new();

    internal LspTestRig()
    {
        Directory.CreateDirectory(Path.Combine(Dir, "proj", "src"));
        File.WriteAllText(Path.Combine(Dir, "proj", "app.fake.toml"), "");
        _script = FakeLspServer.Write(Dir);
        SetEnv("FAKE_LSP_LOG_DIR", Dir);
        LspManager.WorkspaceRoot = () => Dir;
    }

    public void Dispose()
    {
        LspManager.Reset();
        LspManager.Config = () => MuxSwarm.App.Config.Lsp;
        LspManager.WorkspaceRoot = () => MuxSwarm.Engine.PlatformContext.WorkspaceRoot;
        foreach (var (k, v) in _envBefore) Environment.SetEnvironmentVariable(k, v);
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal void SetEnv(string key, string? value)
    {
        if (!_envBefore.ContainsKey(key)) _envBefore[key] = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
    }

    internal LspConfig Use(bool enabled = true, string command = "", int waitMs = 3000)
    {
        var cfg = new LspConfig
        {
            Enabled = enabled,
            DiagnosticsWaitMs = waitMs,
            Servers =
            {
                ["fake"] = new LspServerConfig
                {
                    Command = command.Length > 0 ? command : FakeLspServer.Python,
                    Args = command.Length > 0 ? [] : ["-u", _script],
                    Extensions = ["fake", ".fk"],
                    LanguageIds = { [".fk"] = "fakelang" },
                    RootMarkers = ["*.fake.toml"],
                },
            },
        };
        LspManager.Config = () => cfg;
        return cfg;
    }

    internal string Src(string name, string text)
    {
        var p = Path.Combine(Dir, "proj", "src", name);
        File.WriteAllText(p, text);
        return p;
    }
}
