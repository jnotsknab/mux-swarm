using MuxSwarm.Engine;
using MuxSwarm.Engine.Extensions;
using MuxSwarm.Engine.NativeTools;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// Extension processes inside the configured Mux sandbox. The real-sandbox test runs where bwrap
// works (Linux with bubblewrap installed) and returns early elsewhere, like the other bwrap tests.
[Collection("ConsoleState")]   // ExtensionHost.Running is process-global
public sealed class ExtensionSandboxTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mux-extsbx-" + Guid.NewGuid().ToString("N"));
    private static readonly string Python = OperatingSystem.IsWindows() ? "python" : "python3";

    public ExtensionSandboxTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        ExtensionHost.StopAll();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { Directory.Delete(ExtensionProcess.DataDirectory("sbxtest"), recursive: true); } catch (IOException) { }
    }

    private static SandboxSpec Spec(SandboxKind kind, string backend, bool replStdio = false) =>
        new() { Kind = kind, Backend = backend, Binary = backend, Image = "", CustomTemplate = "wrap {cmd}", CustomReplStdio = replStdio };

    [Fact]
    public void Host_RunsTheCommandItself_InTheExtensionFolder()
    {
        var (file, args, work, session) = ExtensionProcess.BuildStart("sbxtest", _dir, "python", ["-u", "main.py"], null);
        Assert.Equal(("python", (string?)null, _dir, (ISessionSandbox?)null), (file, args, work, session));
    }

    [Fact]
    public void Wrapper_WrapsTheCommand_WorksInTheDataDir_ExtensionFolderReadOnly()
    {
        if (OperatingSystem.IsWindows()) return;   // wrapper backends are Linux/macOS only (Windows paths are refused)
        var (file, args, work, session) = ExtensionProcess.BuildStart("sbxtest", _dir, "/usr/bin/python3", ["-u", "main.py"],
            Spec(SandboxKind.Wrapper, "bwrap"));
        string data = ExtensionProcess.DataDirectory("sbxtest");
        Assert.Equal("bwrap", file);
        Assert.Equal(data, work);
        Assert.Null(session);
        Assert.Contains($"--bind {SandboxBackend.ArgvToken(data)} {SandboxBackend.ArgvToken(data)}", args);
        Assert.DoesNotContain($"--bind {SandboxBackend.ArgvToken(_dir)}", args);   // extension folder stays read-only
        Assert.Contains($"--ro-bind-try {_dir} {_dir}", args);                     // ...and visible even under the private /tmp
        Assert.EndsWith("/usr/bin/python3 -u main.py", args);
        Assert.True(Directory.Exists(data));
    }

    [Fact]
    public void Custom_NeedsReplStdio()
    {
        Assert.Throws<SandboxException>(() => ExtensionProcess.BuildStart("sbxtest", _dir, "python", [], Spec(SandboxKind.Custom, "custom")));
        var (file, args, _, _) = ExtensionProcess.BuildStart("sbxtest", _dir, "python", ["a b"], Spec(SandboxKind.Custom, "custom", replStdio: true));
        Assert.Equal(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh", file);
        Assert.Contains("wrap python", args);
        Assert.Contains("a b", args);
    }

    [Fact]
    public void ContainerExec_IsInteractive_InWork_WithQuotedArgv()
    {
        Assert.Equal("exec -i -w /work mux_sbx_x python -u /ext/main.py \"a b\"",
            OciSandbox.InteractiveArgs("mux_sbx_x", ["python", "-u", "/ext/main.py", "a b"]));
    }

    [Fact]
    public void UnusableSandbox_NeverFallsBackToHost_UnlessExempted()
    {
        File.WriteAllText(Path.Combine(_dir, "main.py"), "import sys\nfor l in sys.stdin: pass\n");
        var ext = new ExtensionRegistry.Extension("sbxtest", null, null, _dir, [])
        {
            Process = new ExtensionProcessSpec { Command = Python, Args = ["-u", "main.py"] },
        };
        var broken = new SandboxConfig { Backend = "no-such-backend" };

        var problems = ExtensionHost.Restart([ext], new HashSet<string> { "sbxtest" }, null, broken);
        Assert.Empty(ExtensionHost.Running);
        Assert.Contains(problems, p => p.Contains("sandbox is not usable") && p.Contains("extensions.unsandboxed"));

        problems = ExtensionHost.Restart([ext], new HashSet<string> { "sbxtest" }, new HashSet<string> { "sbxtest" }, broken);
        Assert.Empty(problems);
        Assert.Equal("host", Assert.Single(ExtensionHost.Running).Isolation);
    }

    [Fact]
    public async Task Bwrap_ProcessWritesItsDataDir_ButNotTheExtensionFolderOrHome()
    {
        if (!OperatingSystem.IsLinux() || SandboxBackend.Validate(new SandboxConfig { Backend = "bwrap" }) is not null) return;
        // Writes a file in its cwd (the data dir), then tries the extension folder and $HOME; reports via a request.
        File.WriteAllText(Path.Combine(_dir, "main.py"), """
            import sys, json, os
            def attempt(p):
                try:
                    open(p, "w").write("x"); return "wrote"
                except Exception as e:
                    return type(e).__name__
            for line in sys.stdin:
                msg = json.loads(line)
                if msg.get("method") == "initialize": init = msg["params"]
                if "id" in msg:
                    r = {"cwd": attempt("data.txt"), "ext": attempt(os.path.join(init["dir"], "x.txt")),
                         "home": attempt(os.path.expanduser("~/mux-ext-escape.txt")), "dataDir": init.get("dataDir")}
                    print(json.dumps({"jsonrpc": "2.0", "id": msg["id"], "result": r}), flush=True)
            """);
        var ext = new ExtensionRegistry.Extension("sbxtest", null, null, _dir, [])
        {
            Process = new ExtensionProcessSpec { Command = "/usr/bin/python3", Args = ["-u", Path.Combine(_dir, "main.py")] },
        };
        var problems = ExtensionHost.Restart([ext], new HashSet<string> { "sbxtest" }, null, new SandboxConfig { Backend = "bwrap", Network = false });
        Assert.Empty(problems);
        var proc = Assert.Single(ExtensionHost.Running);
        Assert.Equal("bwrap", proc.Isolation);

        var reply = await proc.RequestAsync("probe", null, TimeSpan.FromSeconds(20));
        var r = reply.GetProperty("result");
        Assert.Equal("wrote", r.GetProperty("cwd").GetString());
        Assert.NotEqual("wrote", r.GetProperty("ext").GetString());
        Assert.NotEqual("wrote", r.GetProperty("home").GetString());
        Assert.Equal(ExtensionProcess.DataDirectory("sbxtest"), r.GetProperty("dataDir").GetString());
        Assert.True(File.Exists(Path.Combine(ExtensionProcess.DataDirectory("sbxtest"), "data.txt")));
        Assert.False(File.Exists(Path.Combine(_dir, "x.txt")));
    }
}
