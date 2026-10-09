using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using MuxSwarm.Engine;
using MuxSwarm.Engine.NativeTools;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Live proof that the Python REPL worker AND shell jobs are confined by the Linux wrapper backends
/// (v0.15.2 #1/#2). Needs bwrap/firejail + python3 on Linux, so it only runs when MUX_WRAPPER_LIVE=1;
/// otherwise it returns without asserting (same opt-in pattern as SbxSandboxLiveTests).
/// </summary>
[Collection("ConsoleState")]
public class WrapperSandboxLiveTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("MUX_WRAPPER_LIVE") == "1" && OperatingSystem.IsLinux();

    private static async Task<string> Call(string name, object args)
    {
        var fn = (AIFunction)ReplShellTools.Build().First(t => ((AIFunction)t).Name == name);
        var dict = args.GetType().GetProperties().ToDictionary(p => p.Name, p => (object?)p.GetValue(args));
        return (await fn.InvokeAsync(new AIFunctionArguments(dict), CancellationToken.None))?.ToString() ?? "";
    }

    private static async Task<string> Shell(string cmd)
    {
        var started = await Call("execute_command_async", new { command = cmd });
        string id = started.Split('\n')[0].Replace("Job ID:", "").Trim();
        return await Call("wait_job_progress", new { job_id = id, wait_seconds = 30 });
    }

    [Theory]
    [InlineData("bwrap")]
    [InlineData("firejail")]
    public async Task ReplAndShell_AreConfinedByTheWrapper(string backend)
    {
        if (!Enabled) return;
        // firejail nested in another sandbox (WSL, containers) silently runs unconfined: Mux must refuse it.
        if (backend == "firejail" && SandboxBackend.Validate(new SandboxConfig { Backend = backend }) is { } err)
        {
            Console.WriteLine($"[firejail] refused: {err}");
            Assert.Contains("existing sandbox", err);
            ReplShellTools.DisposeAll();
            MuxSwarm.App.Config.Sandbox = new SandboxConfig { Backend = backend };
            try { Assert.Contains("SANDBOX ERROR", await Call("repl_shell_exec", new { code = "print('ran')" })); }
            finally { MuxSwarm.App.Config.Sandbox = new SandboxConfig(); ReplShellTools.DisposeAll(); }
            return;
        }
        ReplShellTools.DisposeAll();
        MuxSwarm.App.Config.Sandbox = new SandboxConfig { Backend = backend, Network = false };
        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string marker = System.IO.Path.Combine(home, ".mux_wrapper_live_secret");
            System.IO.File.WriteAllText(marker, "host-secret");
            try
            {
                // The reporter's probe: host secrets + network must be out of reach for the REPL.
                string r = await Call("repl_shell_exec", new { code =
                    "import os, socket\n" +
                    $"try:\n    print('SECRET', open({System.Text.Json.JsonSerializer.Serialize(marker)}).read())\nexcept Exception as e:\n    print('SECRET-BLOCKED', type(e).__name__)\n" +
                    "try:\n    socket.create_connection(('1.1.1.1', 443), timeout=4); print('NET-OPEN')\nexcept Exception as e:\n    print('NET-BLOCKED', type(e).__name__)\n" +
                    "persist = 7\nprint('PID', os.getpid())" });
                Console.WriteLine($"[{backend}] repl: {r}");
                Assert.Contains("NET-BLOCKED", r);
                Assert.DoesNotContain("NET-OPEN", r);
                if (backend == "firejail") Assert.Contains("SECRET-BLOCKED", r);   // bwrap ro-binds / (read-only, not hidden)
                Assert.Contains("8", await Call("repl_shell_exec", new { code = "print(persist + 1)" }));   // state persists through the wrapper

                // bwrap mounts / read-only: the REPL must NOT be able to write host files outside its work dir.
                string w = await Call("repl_shell_exec", new { code =
                    $"try:\n    open({System.Text.Json.JsonSerializer.Serialize(marker)}, 'w').write('pwned'); print('WRITE-OK')\nexcept Exception as e:\n    print('WRITE-BLOCKED', type(e).__name__)" });
                Console.WriteLine($"[{backend}] write: {w}");
                Assert.Contains("WRITE-BLOCKED", w);
                Assert.Equal("host-secret", System.IO.File.ReadAllText(marker));

                // #2: quoted / multi-word shell commands work through the wrapper.
                string s = await Call("execute_command_async", new { command = "sleep 2; echo 'quoted test' \"$((40+2))\"" });
                string id = s.Split('\n')[0].Replace("Job ID:", "").Trim();
                string o = await Call("wait_job_progress", new { job_id = id, wait_seconds = 30 });
                Console.WriteLine($"[{backend}] shell: {o}");
                Assert.Contains("quoted test 42", o);
                Assert.Contains("exit 0", o);
            }
            finally { System.IO.File.Delete(marker); }
        }
        finally { MuxSwarm.App.Config.Sandbox = new SandboxConfig(); ReplShellTools.DisposeAll(); }
    }

    [Fact]
    public async Task Bwrap_InstallPackage_RunsWrapped_AndRespectsNetwork()
    {
        if (!Enabled) return;
        // A package never fetched before (unique per run is impossible on PyPI, so pick one unlikely to be
        // cached and wipe the session's uv cache first): network off must fail, network on must install.
        const string pkg = "tomli-w";
        foreach (bool net in new[] { false, true })
        {
            ReplShellTools.DisposeAll();
            MuxSwarm.App.Config.Sandbox = new SandboxConfig { Backend = "bwrap", Network = net };
            try
            {
                // Start each pass without the package. The venv persists across runs and uv venvs have no pip, so
                // delete the installed files directly (works inside any wrapper, no uv/pip needed in the sandbox).
                Assert.Contains("ok", await Call("repl_shell_exec", new { code =
                    "import shutil, os, glob, sysconfig\n" +
                    "shutil.rmtree(os.path.join(os.getcwd(), '.uv-cache'), ignore_errors=True)\n" +
                    "site = sysconfig.get_paths()['purelib']\n" +
                    "for p in glob.glob(os.path.join(site, 'tomli_w*')): shutil.rmtree(p, ignore_errors=True)\n" +
                    "print('ok')" }));
                string s = await Call("install_package_async", new { package = pkg });
                string id = s.Split('\n')[0].Replace("Job ID:", "").Trim();
                string o = "";
                for (int i = 0; i < 6 && !o.Contains("Status: completed") && !o.Contains("Status: failed"); i++)
                    o = await Call("wait_job_progress", new { job_id = id, wait_seconds = 20 });
                string imp = await Call("repl_shell_exec", new { code = "import importlib.util; print('PKG', importlib.util.find_spec('tomli_w') is not None)" });
                Console.WriteLine($"[bwrap net={net}] install: {o.Replace('\n', ' ')} | {imp.Replace('\n', ' ')}");
                if (net) { Assert.Contains("exit 0", o); Assert.Contains("PKG True", imp); }
                else { Assert.DoesNotContain("exit 0", o); Assert.Contains("PKG False", imp); }
            }
            finally { MuxSwarm.App.Config.Sandbox = new SandboxConfig(); ReplShellTools.DisposeAll(); }
        }
    }
}
