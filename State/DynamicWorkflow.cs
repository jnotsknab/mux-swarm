using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using MuxSwarm.Utils;

namespace MuxSwarm.State;

/// <summary>
/// Dynamic workflows (v0.12.4, ultracode parity): the agent (or /workflow dynamic) authors a
/// PYTHON DRIVER SCRIPT that uses the muxswarm SDK to spawn separate headless mux processes
/// (one per task), so orchestration state lives in script variables - never in model context.
/// The script reports progress into the run journal (status.ndjson) that
/// <see cref="WorkflowRunRegistry"/> tails for the /workflows viewer.
///
/// Contract with the generated script (kept deliberately tiny + stdlib-only on our side):
///   - MUX_BINARY / MUX_INSTALL_DIR / MUX_RUN_DIR / MUX_MAX_PARALLEL env vars are provided.
///   - The script writes manifest.json (shape) then appends NDJSON status lines, and exits 0
///     on success. A terminal {"run":"done"} line is expected; driver exit is the fallback.
/// </summary>
public static class DynamicWorkflow
{
    /// <summary>Resolve a python launcher, or null when none is on PATH.</summary>
    public static string? ResolvePython()
    {
        foreach (var cand in OperatingSystem.IsWindows()
                     ? new[] { "python.exe", "python3.exe", "py.exe" }
                     : new[] { "python3", "python" })
        {
            try
            {
                var psi = new ProcessStartInfo(cand, "--version")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                using var p = Process.Start(psi);
                if (p is null) continue;
                p.WaitForExit(4000);
                if (p.ExitCode == 0) return cand;
            }
            catch { /* try next */ }
        }
        return null;
    }

    /// <summary>The scripting contract + example fed to the model when it authors a driver
    /// script (giga run_dynamic_workflow and /workflow dynamic share it).</summary>
    public static string ScriptContract(int maxParallel) => $$"""
        ## Dynamic-workflow driver script contract (Python, muxswarm SDK)
        Author a COMPLETE Python script. It runs OUTSIDE your session and drives separate mux
        processes; its variables hold all intermediate state (results never enter your context).
        Requirements:
        - Use only: python stdlib + `muxswarm` (importable; MUX_BINARY env points at the engine).
        - Read env: MUX_RUN_DIR (write manifest/status there), MUX_BINARY, MUX_INSTALL_DIR,
          MUX_MAX_PARALLEL (concurrency cap = {{maxParallel}}).
        - FIRST write manifest.json: {"id","name","mode":"dynamic","sections":[{"name",
          "tasks":[{"id","agent","label","status":"pending"}]}]}.
        - Per task: append {"task":"<id>","status":"running"} to status.ndjson, run the agent via
          `res = await mux.run_goal(goal, mode="agent", agent=<agent>, timeout=...)`, then append
          {"task":"<id>","status":"done","detail":"<short result>"} (or "failed").
          RunResult has NO `.text` attribute. The output is `res.final_summary or res.streamed_text`
          (summary of the last task_complete, else the concatenated stream); `res.ok` / `res.errors`
          for success checks. Using `res.text` raises AttributeError and fails the task.
        - Bound concurrency with asyncio.Semaphore(MUX_MAX_PARALLEL). Feed results forward between
          sections through variables. Append {"run":"done"} (or {"run":"failed","error":...}) LAST.
        - Deterministic + idempotent where possible; no interactive input; exit 0 on success.
        Skeleton:
        ```python
        import asyncio, json, os, sys
        from muxswarm import MuxSwarm
        RUN = os.environ["MUX_RUN_DIR"]; MAXP = int(os.environ.get("MUX_MAX_PARALLEL", "4"))
        def emit(**kw):
            with open(os.path.join(RUN, "status.ndjson"), "a", encoding="utf-8") as f:
                f.write(json.dumps(kw) + "\n")
        async def main():
            mux = MuxSwarm(binary=os.environ["MUX_BINARY"], install_dir=os.environ["MUX_INSTALL_DIR"])
            manifest = {"id": os.path.basename(RUN), "name": "my-workflow", "mode": "dynamic",
                        "sections": [{"name": "Research", "tasks": [
                            {"id": "t1", "agent": "WebAgent", "label": "research X", "status": "pending"}]}]}
            json.dump(manifest, open(os.path.join(RUN, "manifest.json"), "w", encoding="utf-8"))
            sem = asyncio.Semaphore(MAXP)
            async def run_task(tid, agent, goal):
                async with sem:
                    emit(task=tid, status="running")
                    try:
                        res = await mux.run_goal(goal, mode="agent", agent=agent, timeout=600)
                        out = res.final_summary or res.streamed_text or ""
                        emit(task=tid, status="done", detail=out[:160])
                        return out
                    except Exception as e:
                        emit(task=tid, status="failed", detail=str(e)[:160]); raise
            r1 = await run_task("t1", "WebAgent", "research X")
            emit(run="done")
        asyncio.run(main())
        ```
        """;

    /// <summary>
    /// Generate a driver script for <paramref name="goal"/> with the session model, save it into
    /// a fresh run dir, launch it detached, and register the run. Returns a user-facing summary.
    /// </summary>
    public static async Task<string> GenerateAndLaunchAsync(
        string name, string goal, IChatClient client, ChatOptions? opts, CancellationToken ct)
    {
        var py = ResolvePython();
        if (py is null)
            return "[workflow] Dynamic mode needs python on PATH (drives the muxswarm SDK script). Install python 3.9+ or use /workflow static.";

        int maxPar = App.MaxDegreeParallelism > 0 ? App.MaxDegreeParallelism : 4;
        string script;
        try
        {
            var prompt = $"Write ONLY a complete Python driver script (no markdown fences, no prose) for this workflow goal:\n{goal}\n\n{ScriptContract(maxPar)}\nAvailable agents: {string.Join(", ", Common.ParseAgentDefinitions(App.SwarmConfig!).Select(d => d.Name))}.";
            var resp = await client.GetResponseAsync(
                new[] { new ChatMessage(ChatRole.User, prompt) }, opts, ct);
            script = resp.Text ?? "";
        }
        catch (Exception ex) { return $"[workflow] Script generation failed: {ex.Message}"; }
        script = StripFences(script);
        if (string.IsNullOrWhiteSpace(script) || !script.Contains("muxswarm"))
            return "[workflow] Generated script was empty or did not use the muxswarm SDK. Retry with a clearer goal.";

        return Launch(name, script, py, maxPar);
    }

    /// <summary>Launch an already-authored driver script (giga hands the script text straight in).</summary>
    public static string Launch(string name, string script, string? python = null, int? maxParallel = null)
    {
        var py = python ?? ResolvePython();
        if (py is null)
            return "[workflow] Dynamic mode needs python on PATH. Install python 3.9+ or use static workflows.";
        int maxPar = maxParallel ?? (App.MaxDegreeParallelism > 0 ? App.MaxDegreeParallelism : 4);

        var (id, dir) = WorkflowRunRegistry.NewRunDir(name);
        var scriptPath = Path.Combine(dir, "driver.py");
        File.WriteAllText(scriptPath, script, new UTF8Encoding(false));

        var psi = new ProcessStartInfo(py, $"\"{scriptPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = dir,
        };
        psi.Environment["MUX_BINARY"] = Environment.ProcessPath ?? Path.Combine(PlatformContext.BaseDirectory, OperatingSystem.IsWindows() ? "MuxSwarm.exe" : "MuxSwarm");
        psi.Environment["MUX_INSTALL_DIR"] = PlatformContext.BaseDirectory;
        psi.Environment["MUX_RUN_DIR"] = dir;
        psi.Environment["MUX_MAX_PARALLEL"] = maxPar.ToString();

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("python did not start");
            // Drain child stdio to the run dir so a failing script leaves a readable log and the
            // pipes never fill (the TUI must NOT inherit them - see the /login reflex).
            var logPath = Path.Combine(dir, "driver.log");
            _ = Task.Run(async () =>
            {
                using var w = new StreamWriter(logPath, append: true, new UTF8Encoding(false));
                var so = proc.StandardOutput.ReadToEndAsync();
                var se = proc.StandardError.ReadToEndAsync();
                await Task.WhenAll(so, se);
                await w.WriteLineAsync(so.Result);
                if (!string.IsNullOrWhiteSpace(se.Result)) await w.WriteLineAsync("--- STDERR ---\n" + se.Result);
            });
        }
        catch (Exception ex) { return $"[workflow] Could not launch driver: {ex.Message}"; }

        WorkflowRunRegistry.Register(new WorkflowRun
        {
            Id = id, Name = name, Mode = "dynamic", RunDir = dir, Driver = proc,
        });
        return $"[workflow] Dynamic run '{name}' launched (id {id}, driver pid {proc.Id}, cap {maxPar}). Watch it with /workflows; script + journal in {dir}.";
    }

    private static string StripFences(string s)
    {
        var t = (s ?? "").Trim();
        if (t.StartsWith("```"))
        {
            int nl = t.IndexOf('\n');
            if (nl >= 0) t = t[(nl + 1)..];
            int end = t.LastIndexOf("```", StringComparison.Ordinal);
            if (end >= 0) t = t[..end];
        }
        return t.Trim();
    }
}
