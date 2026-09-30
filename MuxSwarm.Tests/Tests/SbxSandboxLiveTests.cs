using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using MuxSwarm.Engine;
using MuxSwarm.Engine.NativeTools;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Live end-to-end check of the <c>docker</c> (Docker Sandboxes microVM) and <c>docker-container</c> backends
/// through the real native tool surface: Python worker, shell job, /work + /host layout, read-only mount, air-gapped network and
/// teardown; both must honour the same contract. Needs a signed-in <c>sbx</c> and a running Docker engine,
/// so it only runs when <c>MUX_SBX_LIVE=1</c>; otherwise it returns without asserting.
/// </summary>
[Collection("ConsoleState")]
public class SbxSandboxLiveTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("MUX_SBX_LIVE") == "1";

    private static async Task<string> Call(string name, object args)
    {
        var fn = (AIFunction)ReplShellTools.Build().First(t => ((AIFunction)t).Name == name);
        var dict = args.GetType().GetProperties().ToDictionary(p => p.Name, p => (object?)p.GetValue(args));
        return (await fn.InvokeAsync(new AIFunctionArguments(dict), CancellationToken.None))?.ToString() ?? "";
    }

    private static async Task<string> RunJob(string command)
    {
        string started = await Call("execute_command_async", new { command });
        string id = started.Split('\n')[0].Replace("Job ID:", "").Trim();
        for (int i = 0; i < 60; i++)
        {
            string s = await Call("check_job_status", new { job_id = id });
            if (!s.Contains("Status: running") && !s.Contains("Status: starting")) return s;
            await Task.Delay(500);
        }
        return await Call("check_job_status", new { job_id = id });
    }

    [Theory]
    [InlineData("docker")]
    [InlineData("docker-container")]
    public async Task Docker_Backends_RunShellAndPythonInsideTheSandbox(string backend)
    {
        if (!Enabled) return;
        string root = Path.Combine(Path.GetTempPath(), "mux-sbx-live-" + Guid.NewGuid().ToString("N")[..6]);
        string rw = Directory.CreateDirectory(Path.Combine(root, "proj")).FullName;
        string ro = Directory.CreateDirectory(Path.Combine(root, "refs")).FullName;
        File.WriteAllText(Path.Combine(ro, "ref.txt"), "reference");
        var prevFs = App.Config.Filesystem;
        App.Config.Sandbox = new SandboxConfig { Backend = backend, Image = "python:3.12-slim", Network = false };
        App.Config.Filesystem = new FilesystemConfig { SecurityMode = "standard", AllowedPaths = { rw, ro } };
        string key = "live_" + Guid.NewGuid().ToString("N")[..6];
        try
        {
            using (ReplShellTools.BeginScope(key))
            {
                // Python worker runs in the VM: own kernel, cwd /work, state persists across calls.
                // /work is a link to the session work dir, so the worker's cwd is that dir's guest path.
                string py = await Call("repl_shell_exec", new { code = "import os, platform\nx = 41\nopen('/work/py.txt', 'w').write('py')\nprint(platform.release(), os.getcwd() == os.path.realpath('/work'))" });
                Assert.DoesNotContain("SANDBOX ERROR", py);
                Assert.Contains("True", py);
                Assert.Contains("42", await Call("repl_shell_exec", new { code = "print(x + 1)" }));

                // Shell job: writes land on the host through /host/<leaf>; the read-only mount rejects writes.
                string sh = await RunJob("echo from-vm > /host/proj/out.txt && cat /host/refs/ref.txt && (echo x > /host/refs/no.txt || echo ro-denied)");
                Assert.Contains("reference", sh);
                Assert.Contains("ro-denied", sh);
                Assert.Equal("from-vm", File.ReadAllText(Path.Combine(rw, "out.txt")).Trim());
                Assert.False(File.Exists(Path.Combine(ro, "no.txt")));

                // network: false => egress denied.
                string net = await RunJob("python -c \"import urllib.request as u; print(u.urlopen('https://pypi.org', timeout=8).status)\" || echo net-blocked");
                Assert.Contains("net-blocked", net);
            }   // scope dispose tears the session (and its microVM) down
            if (backend == "docker")
            {
                string ls = OciSandbox.Run(SandboxBackend.SbxBinary(), "ls --json", allowFail: true).outp;
                Assert.DoesNotContain("mux-live-", ls);   // torn down with the session
            }
        }
        finally
        {
            App.Config.Sandbox = new SandboxConfig();
            App.Config.Filesystem = prevFs;
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
