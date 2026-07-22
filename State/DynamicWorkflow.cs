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
    /// <summary>
    /// Ensure the muxswarm SDK is importable by <paramref name="py"/>. Resolution order:
    ///   1. Already importable -> ready.
    ///   2. MUX_SDK_PATH env var (or a sibling "sdk" folder in the install dir) containing a
    ///      muxswarm package -> returned as a PYTHONPATH injection for the child.
    ///   3. Best-effort `pip install muxswarm` (bounded), then re-probe.
    /// Returns (ready, pythonPathOverride, error): ready=false + error when nothing worked -
    /// the caller surfaces the error instead of launching a doomed driver.
    /// </summary>
    public static (bool Ready, string? PythonPath, string? Error) EnsureSdk(string py)
    {
        if (ProbeImport(py, null)) return (true, null, null);

        // Local-clone fallback: an explicit MUX_SDK_PATH, or an "sdk" folder shipped/cloned
        // beside the engine install.
        foreach (var cand in new[]
                 {
                     Environment.GetEnvironmentVariable("MUX_SDK_PATH"),
                     Path.Combine(PlatformContext.BaseDirectory, "sdk"),
                 })
        {
            if (SdkPathIsUsable(cand) && ProbeImport(py, cand!)) return (true, cand, null);
        }

        // Auto-install from PyPI, best-effort and bounded.
        try
        {
            var psi = new ProcessStartInfo(py, "-m pip install --quiet muxswarm")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi);
            if (p is not null && p.WaitForExit(120_000) && p.ExitCode == 0 && ProbeImport(py, null))
                return (true, null, null);
        }
        catch { /* fall through to the error */ }

        return (false, null,
            "[workflow] The muxswarm SDK is not importable by the resolved python and auto-install failed. " +
            "Fix one of: `pip install muxswarm`, set MUX_SDK_PATH to a local mux-swarm-sdk clone, " +
            "or place the SDK under <install-dir>/sdk. Dynamic mode needs it; /workflow static does not.");
    }

    /// <summary>True when <paramref name="dir"/> looks like an importable SDK root
    /// (contains muxswarm/__init__.py).</summary>
    internal static bool SdkPathIsUsable(string? dir)
        => !string.IsNullOrWhiteSpace(dir)
           && File.Exists(Path.Combine(dir!, "muxswarm", "__init__.py"));

    private static bool ProbeImport(string py, string? pythonPath)
    {
        try
        {
            var psi = new ProcessStartInfo(py, "-c \"import muxswarm\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            if (pythonPath is not null)
            {
                var existing = Environment.GetEnvironmentVariable("PYTHONPATH");
                psi.Environment["PYTHONPATH"] = string.IsNullOrEmpty(existing)
                    ? pythonPath
                    : pythonPath + Path.PathSeparator + existing;
            }
            using var p = Process.Start(psi);
            return p is not null && p.WaitForExit(15_000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

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
          MUX_MAX_PARALLEL (concurrency cap = {{maxParallel}}), MUX_CFG, MUX_SWARMCFG.
        - MANDATORY: construct the client as MuxSwarm(binary=os.environ["MUX_BINARY"],
          install_dir=os.environ["MUX_INSTALL_DIR"], cfg=os.environ.get("MUX_CFG"),
          swarmcfg=os.environ.get("MUX_SWARMCFG")). Omitting cfg/swarmcfg makes every child
          engine drop into first-run setup and fail with "No endpoint provided. Setup failed."
        - FIRST write manifest.json: {"id","name","mode":"dynamic","sections":[{"name",
          "tasks":[{"id","agent","label","status":"pending"}]}]}.
        - Per task: append {"task":"<id>","status":"running"} to status.ndjson, run the agent via
          `res = await mux.run_goal(goal, mode="agent", agent=<agent>, timeout=...)`, then append
          {"task":"<id>","status":"done","detail":"<short result>"} (or "failed").
          RunResult has NO `.text` attribute. The output is `res.final_summary or res.streamed_text`
          (summary of the last task_complete, else the concatenated stream); `res.ok` / `res.errors`
          for success checks. Using `res.text` raises AttributeError and fails the task.
        - LONG GOALS / FEEDING RESULTS FORWARD (MANDATORY): NEVER inline large text (prior phase
          results, corpora) into the goal string - on Windows the child argv limit is ~32k chars
          and exceeding it fails with [WinError 206]. Instead write the full prompt to a file in
          MUX_RUN_DIR (e.g. goal_synth.txt) and pass THE FILE PATH as the goal - the engine
          detects an existing file path and reads its contents as the goal. Rule of thumb: any
          goal over ~2000 chars goes through a file.
        - Telemetry (optional but preferred): include secs (wall seconds, int), tools (count of
          TOOL_CALL events), tokens (int, from the last agent_turn_end event's "tokens" field in
          res.events raw payloads), model (agent's model id if known) in the done/failed status
          lines - the /workflows viewer renders them per task.
        - PER-TASK OUTPUT FILE (MANDATORY): stream each task's output to MUX_RUN_DIR/task_<id>.out
          LIVE - pass an on_event callback to run_goal that appends every stream-event's text to
          the file as it arrives (open in append mode per write, no buffering held across awaits).
          The /workflows viewer tails this file when the user expands the task row, so this is
          what makes live progress and full output visible. The 160-char detail field is only the
          collapsed-row summary; the .out file is the real transcript.
        - Bound concurrency with asyncio.Semaphore(MUX_MAX_PARALLEL). Feed results forward between
          sections through variables. Append {"run":"done"} (or {"run":"failed","error":...}) LAST.
        - Deterministic + idempotent where possible; no interactive input; exit 0 on success.
        Skeleton:
        ```python
        import asyncio, json, os, sys, time
        from muxswarm import MuxSwarm
        RUN = os.environ["MUX_RUN_DIR"]; MAXP = int(os.environ.get("MUX_MAX_PARALLEL", "4"))
        def emit(**kw):
            with open(os.path.join(RUN, "status.ndjson"), "a", encoding="utf-8") as f:
                f.write(json.dumps(kw) + "\n")
        async def main():
            mux = MuxSwarm(binary=os.environ["MUX_BINARY"], install_dir=os.environ["MUX_INSTALL_DIR"],
                           cfg=os.environ.get("MUX_CFG"), swarmcfg=os.environ.get("MUX_SWARMCFG"))
            manifest = {"id": os.path.basename(RUN), "name": "my-workflow", "mode": "dynamic",
                        "sections": [{"name": "Research", "tasks": [
                            {"id": "t1", "agent": "WebAgent", "label": "research X", "status": "pending"}]}]}
            json.dump(manifest, open(os.path.join(RUN, "manifest.json"), "w", encoding="utf-8"))
            sem = asyncio.Semaphore(MAXP)
            def goal_arg(tid, text):
                # Long goals MUST go through a file (argv limit); the engine reads file paths.
                if len(text) <= 2000: return text
                p = os.path.join(RUN, f"goal_{tid}.txt")
                with open(p, "w", encoding="utf-8") as f: f.write(text)
                return p
            def out_write(tid, text):
                with open(os.path.join(RUN, f"task_{tid}.out"), "a", encoding="utf-8") as f:
                    f.write(text)
            async def run_task(tid, agent, goal):
                async with sem:
                    emit(task=tid, status="running")
                    t0 = time.monotonic()
                    def on_ev(ev, _tid=tid):
                        if getattr(ev.type, "value", "") == "stream":
                            t = ev.raw.get("text") or ""
                            if t: out_write(_tid, t)
                    try:
                        res = await mux.run_goal(goal_arg(tid, goal), mode="agent", agent=agent,
                                                 on_event=on_ev, timeout=600)
                        out = res.final_summary or res.streamed_text or ""
                        if res.final_summary and res.final_summary not in (res.streamed_text or ""):
                            out_write(tid, "\n" + res.final_summary)
                        tools = sum(1 for ev in res.events if getattr(ev.type, "value", "") == "tool_call")
                        toks = 0
                        for ev in res.events:
                            if getattr(ev.type, "value", "") == "agent_turn_end":
                                toks = ev.raw.get("tokens", toks) or toks
                        emit(task=tid, status="done", detail=out[:160],
                             secs=int(time.monotonic() - t0), tools=tools, tokens=int(toks))
                        return out
                    except Exception as e:
                        emit(task=tid, status="failed", detail=str(e)[:160],
                             secs=int(time.monotonic() - t0)); raise
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

    /// <summary>
    /// Pre-flight contract validation: catches the known-fatal script mistakes BEFORE spawning
    /// so the author gets the contract back as tool feedback instead of a dead run in the
    /// viewer. Returns null when the script passes, else the rejection message.
    /// </summary>
    public static string? ValidateScript(string script)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(script)) problems.Add("script is empty");
        else
        {
            if (!script.Contains("muxswarm")) problems.Add("does not import/use the muxswarm SDK");
            if (!script.Contains("MUX_RUN_DIR")) problems.Add("never reads MUX_RUN_DIR (manifest/status journal would be lost)");
            if (!script.Contains("MUX_CFG")) problems.Add("never passes MUX_CFG/MUX_SWARMCFG to MuxSwarm(...) - every child engine will fail setup with 'No endpoint provided'");
            if (!script.Contains("manifest.json")) problems.Add("never writes manifest.json (the /workflows viewer would show no phases)");
            if (!script.Contains("status.ndjson")) problems.Add("never appends status.ndjson (no live progress)");
            if (!script.Contains(".out")) problems.Add("never writes per-task task_<id>.out output files (expanded rows in /workflows would show nothing)");
            if (script.Contains(".text")) problems.Add("references RunResult .text which does not exist - use final_summary/streamed_text");
        }
        if (problems.Count == 0) return null;
        return "[workflow] Script REJECTED (contract violations):\n- " + string.Join("\n- ", problems)
             + "\nFix the script per the contract below and call the tool again.\n\n";
    }

    /// <summary>Launch an already-authored driver script (giga hands the script text straight in).
    /// The script is contract-validated first; violations return the contract instead of launching.</summary>
    public static string Launch(string name, string script, string? python = null, int? maxParallel = null)
    {
        int capForContract = maxParallel ?? (App.MaxDegreeParallelism > 0 ? App.MaxDegreeParallelism : 4);
        if (ValidateScript(script) is { } rejection)
            return rejection + ScriptContract(capForContract);

        var py = python ?? ResolvePython();
        if (py is null)
            return "[workflow] Dynamic mode needs python on PATH. Install python 3.9+ or use static workflows.";
        var (sdkReady, sdkPythonPath, sdkError) = EnsureSdk(py);
        if (!sdkReady) return sdkError!;
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
        // Children MUST inherit the parent's live config: a test/staged binary's own Configs/
        // folder is empty, and a config-less child drops into first-run setup and dies with
        // "No endpoint provided. Setup failed." (live-observed).
        if (File.Exists(PlatformContext.ConfigPath)) psi.Environment["MUX_CFG"] = PlatformContext.ConfigPath;
        if (File.Exists(PlatformContext.SwarmPath)) psi.Environment["MUX_SWARMCFG"] = PlatformContext.SwarmPath;
        // SDK resolved via a local clone rather than site-packages: inject it for the child.
        if (sdkPythonPath is not null)
        {
            var existingPp = Environment.GetEnvironmentVariable("PYTHONPATH");
            psi.Environment["PYTHONPATH"] = string.IsNullOrEmpty(existingPp)
                ? sdkPythonPath
                : sdkPythonPath + Path.PathSeparator + existingPp;
        }

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
