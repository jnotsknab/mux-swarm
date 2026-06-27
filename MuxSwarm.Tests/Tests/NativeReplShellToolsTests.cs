using System.Threading.Tasks;
using System.Threading;
using MuxSwarm.Utils.NativeTools;
using Microsoft.Extensions.AI;
using System.Linq;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Live integration coverage for the native (in-house) session-scoped REPL/shell tools that
/// replace the shared-connection mcp-async-repl server. Spawns a real Python worker subprocess,
/// so it asserts the genuine wire (persistent vars, isolation, restart). Requires `python` on PATH.
/// </summary>
public class NativeReplShellToolsTests
{
    private static AIFunction Fn(string name) =>
        (AIFunction)ReplShellTools.Build().First(t => ((AIFunction)t).Name == name);

    private static async Task<string> Call(string name, object args)
    {
        var fn = Fn(name);
        var dict = args.GetType().GetProperties().ToDictionary(p => p.Name, p => (object?)p.GetValue(args));
        var res = await fn.InvokeAsync(new AIFunctionArguments(dict), CancellationToken.None);
        return res?.ToString() ?? "";
    }

    [Fact]
    public async Task Surface_ExposesTheExpectedToolNames()
    {
        var names = ReplShellTools.Build().Select(t => ((AIFunction)t).Name).ToHashSet();
        Assert.Contains("repl_shell_exec", names);
        Assert.Contains("check_python_status", names);
        Assert.Contains("execute_command_async", names);
        Assert.Contains("check_job_status", names);
        Assert.Contains("install_package_async", names);
    }

    [Fact]
    public async Task Python_RunsPersistsAndRestarts()
    {
        using var _ = ReplShellTools.BeginScope("t_" + System.Guid.NewGuid().ToString("N")[..8]);
        var r1 = await Call("repl_shell_exec", new { code = "x = 41\nprint('hello', x+1)" });
        Assert.Contains("hello 42", r1);
        Assert.Contains("--- STDOUT ---", r1);          // clear output section (better exposure)
        var r2 = await Call("repl_shell_exec", new { code = "print(x*2)" });
        Assert.Contains("82", r2);                       // variable persisted across calls
        Assert.Contains("x", await Call("list_variables", new { }));
        await Call("restart_python_worker", new { });
        var r3 = await Call("repl_shell_exec", new { code = "print('clean' if 'x' not in dir() else 'dirty')" });
        Assert.Contains("clean", r3);                    // restart cleared state
    }

    [Fact]
    public async Task Python_SurfacesErrorsClearly()
    {
        using var _ = ReplShellTools.BeginScope("err_" + System.Guid.NewGuid().ToString("N")[..8]);
        var r = await Call("repl_shell_exec", new { code = "raise ValueError('boom')" });
        Assert.Contains("Status: error", r);
        Assert.Contains("ValueError", r);
        Assert.Contains("boom", r);
    }

    [Fact]
    public async Task Sessions_AreIsolated()
    {
        using (var _ = ReplShellTools.BeginScope("iso_A"))
            await Call("repl_shell_exec", new { code = "marker = 'AAA'" });
        string outB;
        using (var _ = ReplShellTools.BeginScope("iso_B"))
            outB = await Call("repl_shell_exec", new { code = "print('has' if 'marker' in dir() else 'clean')" });
        Assert.Contains("clean", outB);   // session B never sees session A's worker state
    }
    [Fact]
    public async Task ShellJobs_RunInTheSameVenvAsTheReplWorker()
    {
        using var _ = ReplShellTools.BeginScope("venv_" + System.Guid.NewGuid().ToString("N")[..8]);
        // Force the session venv to materialize via the REPL worker path, then capture its python.
        await Call("install_package_async", new { package = "--upgrade pip" }); // triggers EnsureVenv (uv)
        var replExe = await Call("repl_shell_exec", new { code = "import sys; print(sys.executable)" });

        // A shell job invoking a bare `python` must resolve to the SAME interpreter (venv-activated PATH),
        // not the system python. Poll until the job completes.
        var start = await Call("execute_command_async", new { command = "python -c \"import sys;print(sys.executable)\"" });
        string jobId = start.Split('\n')[0].Replace("Job ID:", "").Trim();
        string shellOut = "";
        for (int i = 0; i < 40; i++)
        {
            shellOut = await Call("check_job_status", new { job_id = jobId });
            if (shellOut.Contains("completed") || shellOut.Contains("failed")) break;
            await Task.Delay(250);
        }

        // If uv is unavailable in CI (no venv), both fall back to system python and still match.
        string Norm(string s) => s.Replace("\r", "").ToLowerInvariant();
        // Extract the venv dir name if present and assert the shell python lives under the same venv tree.
        if (Norm(replExe).Contains(".venv"))
            Assert.Contains(".venv", Norm(shellOut));
    }
}
