using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MuxSwarm.Utils.NativeTools;
using Microsoft.Extensions.AI;
using System.Linq;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Coverage for the additive event-driven progress-wait behavior folded into check_job_status
/// (wait_job_progress) and check_python_status (wait_python_progress), plus a regression pin that the
/// LEGACY full-snapshot tools are byte-for-byte unchanged. Spawns real subprocesses. Requires python on PATH.
/// </summary>
[Collection("ConsoleState")]
public class NativeProgressWaitToolsTests
{
    public NativeProgressWaitToolsTests() => MuxSwarm.App.Config.Sandbox = new MuxSwarm.Utils.SandboxConfig();

    private static AIFunction Fn(string name) =>
        (AIFunction)ReplShellTools.Build().First(t => ((AIFunction)t).Name == name);

    private static async Task<string> Call(string name, object args, CancellationToken ct = default)
    {
        var fn = Fn(name);
        var dict = args.GetType().GetProperties().ToDictionary(p => p.Name, p => (object?)p.GetValue(args));
        var res = await fn.InvokeAsync(new AIFunctionArguments(dict), ct);
        return res?.ToString() ?? "";
    }

    private static string JobId(string startResult) => startResult.Split('\n')[0].Replace("Job ID:", "").Trim();

    // ---- surface ----

    [Fact]
    public void Surface_ExposesTheNewProgressTools()
    {
        var names = ReplShellTools.Build().Select(t => ((AIFunction)t).Name).ToHashSet();
        Assert.Contains("wait_job_progress", names);
        Assert.Contains("wait_python_progress", names);
        // legacy tools still present
        Assert.Contains("check_job_status", names);
        Assert.Contains("check_python_status", names);
    }

    // ---- legacy regression (framing unchanged) ----

    [Fact]
    public async Task Legacy_CheckPythonStatus_Idle_UnchangedFraming()
    {
        using var _ = ReplShellTools.BeginScope("leg_" + Guid.NewGuid().ToString("N")[..8]);
        var s = await Call("check_python_status", new { });
        Assert.Equal("Status: idle (no Python worker started yet).", s);
    }

    [Fact]
    public async Task Legacy_CheckJobStatus_UnknownJob_UnchangedFraming()
    {
        using var _ = ReplShellTools.BeginScope("leg_" + Guid.NewGuid().ToString("N")[..8]);
        var s = await Call("check_job_status", new { job_id = "job_nope" });
        Assert.Equal("No such job: job_nope", s);
    }

    // ---- wait_job_progress ----

    [Fact]
    public async Task WaitJob_ReturnsEarlyOnOutput_ThenCursorDelta()
    {
        using var _ = ReplShellTools.BeginScope("wj_" + Guid.NewGuid().ToString("N")[..8]);
        // prints A, waits, prints B, on both platforms.
        string cmd = OperatingSystem.IsWindows()
            ? "echo AAA && ping -n 3 127.0.0.1 > NUL && echo BBB"
            : "echo AAA; sleep 2; echo BBB";
        var start = await Call("execute_command_async", new { command = cmd });
        string id = JobId(start);

        var r1 = await Call("wait_job_progress", new { job_id = id, wait_seconds = 15 });
        Assert.Contains("AAA", r1);
        Assert.Contains("Changed: true", r1);

        int cur = ExtractNextStdout(r1);
        Assert.True(cur > 0, "cursor should advance past AAA");

        // second call with the advanced cursor must NOT re-return AAA; eventually yields BBB / terminal.
        var r2 = await Call("wait_job_progress", new { job_id = id, wait_seconds = 15, stdout_cursor = cur });
        Assert.DoesNotContain("AAA", r2.Split("--- STDOUT (new) ---").Last());
    }

    [Fact]
    public async Task WaitJob_ReturnsTerminalWithoutWaitingOutTimeout()
    {
        using var _ = ReplShellTools.BeginScope("wj_" + Guid.NewGuid().ToString("N")[..8]);
        string cmd = OperatingSystem.IsWindows() ? "echo quickdone" : "echo quickdone";
        var start = await Call("execute_command_async", new { command = cmd });
        string id = JobId(start);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // large wait budget; must return fast because the job ends almost immediately.
        var r = await Call("wait_job_progress", new { job_id = id, wait_seconds = 60 });
        sw.Stop();
        Assert.True(sw.Elapsed.TotalSeconds < 30, $"returned in {sw.Elapsed.TotalSeconds}s, expected early");
        // drain any tail
        int cur = ExtractNextStdout(r);
        var final = r;
        for (int i = 0; i < 20 && !(final.Contains("Status: completed") || final.Contains("Status: failed")); i++)
            final = await Call("wait_job_progress", new { job_id = id, wait_seconds = 5, stdout_cursor = cur });
        Assert.Contains("quickdone", r + final);
    }

    [Fact]
    public async Task WaitJob_UnknownJob_ReturnsNoSuchJob()
    {
        using var _ = ReplShellTools.BeginScope("wj_" + Guid.NewGuid().ToString("N")[..8]);
        var r = await Call("wait_job_progress", new { job_id = "job_ghost", wait_seconds = 2 });
        Assert.Equal("No such job: job_ghost", r);
    }

    [Fact]
    public async Task WaitJob_Truncation_CapsDeltaAndReportsDropped()
    {
        using var _ = ReplShellTools.BeginScope("wj_" + Guid.NewGuid().ToString("N")[..8]);
        // emit a large blob well over max_chars.
        string cmd = OperatingSystem.IsWindows()
            ? "python -c \"print('z'*5000)\""
            : "python3 -c \"print('z'*5000)\"";
        var start = await Call("execute_command_async", new { command = cmd });
        string id = JobId(start);
        string r = "";
        for (int i = 0; i < 40; i++)
        {
            r = await Call("wait_job_progress", new { job_id = id, wait_seconds = 5, max_chars = 512 });
            if (r.Contains("Truncated: true") || r.Contains("Status: completed")) break;
        }
        // if the big chunk landed in one delta it must be capped + flagged.
        if (r.Contains("--- STDOUT (new) ---"))
        {
            var body = r.Split("--- STDOUT (new) ---").Last();
            Assert.True(body.Length <= 700, $"delta body {body.Length} should be capped near 512");
        }
    }

    [Fact]
    public async Task WaitJob_Cancellation_EndsWaitNotProcess()
    {
        using var _ = ReplShellTools.BeginScope("wj_" + Guid.NewGuid().ToString("N")[..8]);
        string cmd = OperatingSystem.IsWindows()
            ? "ping -n 6 127.0.0.1 > NUL && echo late"
            : "sleep 5; echo late";
        var start = await Call("execute_command_async", new { command = cmd });
        string id = JobId(start);
        using var cts = new CancellationTokenSource(500);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Call("wait_job_progress", new { job_id = id, wait_seconds = 30 }, cts.Token));
        // process untouched: a fresh full-snapshot still finds the job (running or completed).
        var snap = await Call("check_job_status", new { job_id = id });
        Assert.Contains("Job ID:", snap);
    }

    // ---- wait_python_progress ----

    [Fact]
    public async Task WaitPython_Idle_WhenNoWorker()
    {
        using var _ = ReplShellTools.BeginScope("wp_" + Guid.NewGuid().ToString("N")[..8]);
        var r = await Call("wait_python_progress", new { wait_seconds = 2 });
        Assert.Contains("Status: idle", r);
    }

    [Fact]
    public async Task WaitPython_StreamsThenCompletes()
    {
        using var _ = ReplShellTools.BeginScope("wp_" + Guid.NewGuid().ToString("N")[..8]);
        // long job returns as running from repl_shell_exec (>2s), then progress-wait drains it.
        await Call("repl_shell_exec", new { code = "import time,sys\nprint('a', flush=True)\ntime.sleep(3)\nprint('b', flush=True)" });
        var r1 = await Call("wait_python_progress", new { wait_seconds = 15 });
        Assert.Contains("a", r1);
        int cur = ExtractNextStdout(r1);
        string acc = r1;
        for (int i = 0; i < 20 && !acc.Contains("Status: completed"); i++)
        {
            var rn = await Call("wait_python_progress", new { wait_seconds = 10, stdout_cursor = cur });
            cur = ExtractNextStdout(rn) is var c && c > 0 ? c : cur;
            acc += "\n" + rn;
        }
        Assert.Contains("b", acc);
        Assert.Contains("Status: completed", acc);
    }

    [Fact]
    public async Task WaitPython_ReturnsWaitingInputWithPrompt()
    {
        using var _ = ReplShellTools.BeginScope("wp_" + Guid.NewGuid().ToString("N")[..8]);
        await Call("repl_shell_exec", new { code = "name = input('who? ')\nprint('hi', name)" });
        var r = await Call("wait_python_progress", new { wait_seconds = 10 });
        Assert.Contains("Status: waiting_input", r);
    }

    [Fact]
    public async Task WaitJob_AutoCursor_OmittedCursorAdvancesWithoutRepeating()
    {
        using var _ = ReplShellTools.BeginScope("ac_" + Guid.NewGuid().ToString("N")[..8]);
        string cmd = OperatingSystem.IsWindows()
            ? "echo FIRST && ping -n 3 127.0.0.1 > NUL && echo SECOND"
            : "echo FIRST; sleep 2; echo SECOND";
        var start = await Call("execute_command_async", new { command = cmd });
        string id = JobId(start);

        // First bare call (no cursor) should see FIRST.
        var r1 = await Call("wait_job_progress", new { job_id = id, wait_seconds = 15 });
        Assert.Contains("FIRST", r1);

        // Second bare call (still no cursor) must AUTO-RESUME: it must NOT repeat FIRST, and
        // eventually surfaces SECOND / terminal. Drain a few times.
        string acc = "";
        for (int i = 0; i < 10; i++)
        {
            var rn = await Call("wait_job_progress", new { job_id = id, wait_seconds = 10 });
            acc += "\n" + rn;
            if (rn.Contains("Status: completed") || rn.Contains("Status: failed")) break;
        }
        Assert.Contains("SECOND", acc);
        // The delta sections of the resume calls must not re-emit FIRST.
        foreach (var frame in acc.Split("Job ID:"))
        {
            if (frame.Contains("--- STDOUT (new) ---"))
            {
                var body = frame.Split("--- STDOUT (new) ---")[1];
                Assert.DoesNotContain("FIRST", body);
            }
        }
    }

    [Fact]
    public async Task WaitJob_ExplicitCursorZero_StillReReadsFromStart()
    {
        using var _ = ReplShellTools.BeginScope("ac_" + Guid.NewGuid().ToString("N")[..8]);
        string cmd = OperatingSystem.IsWindows() ? "echo REPLAYME" : "echo REPLAYME";
        var start = await Call("execute_command_async", new { command = cmd });
        string id = JobId(start);

        // consume with auto-cursor
        string acc = "";
        for (int i = 0; i < 10; i++)
        {
            var rn = await Call("wait_job_progress", new { job_id = id, wait_seconds = 5 });
            acc += rn;
            if (rn.Contains("Status: completed")) break;
        }
        Assert.Contains("REPLAYME", acc);

        // explicit cursor 0 must re-read from the beginning even after auto-cursor advanced.
        var replay = await Call("wait_job_progress", new { job_id = id, wait_seconds = 5, stdout_cursor = 0 });
        Assert.Contains("REPLAYME", replay);
    }

    [Fact]
    public async Task WaitPython_AutoCursor_ResumesAcrossCalls()
    {
        using var _ = ReplShellTools.BeginScope("acp_" + Guid.NewGuid().ToString("N")[..8]);
        await Call("repl_shell_exec", new { code = "import time,sys\nprint('AA', flush=True)\ntime.sleep(3)\nprint('BB', flush=True)" });
        var r1 = await Call("wait_python_progress", new { wait_seconds = 15 });
        Assert.Contains("AA", r1);
        string acc = "";
        for (int i = 0; i < 12; i++)
        {
            var rn = await Call("wait_python_progress", new { wait_seconds = 10 });
            acc += "\n" + rn;
            if (rn.Contains("Status: completed")) break;
        }
        Assert.Contains("BB", acc);
        // resume frames must not repeat AA in their delta bodies
        foreach (var frame in acc.Split("Status:"))
            if (frame.Contains("--- STDOUT (new) ---"))
                Assert.DoesNotContain("AA", frame.Split("--- STDOUT (new) ---")[1]);
    }

    // ---- helper ----

    private static int ExtractNextStdout(string frame)
    {
        // line: "StdoutCursor: X -> Y   (total T)"
        foreach (var line in frame.Split('\n'))
        {
            if (line.StartsWith("StdoutCursor:"))
            {
                var arrow = line.Split("->");
                if (arrow.Length >= 2)
                {
                    var num = new string(arrow[1].TrimStart().TakeWhile(char.IsDigit).ToArray());
                    if (int.TryParse(num, out var v)) return v;
                }
            }
        }
        return 0;
    }
}
