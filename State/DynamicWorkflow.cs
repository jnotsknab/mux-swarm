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
    /// <summary>
    /// Non-mutating half of <see cref="EnsureSdk"/>: reports whether the SDK is already
    /// importable (directly or via MUX_SDK_PATH / a sibling "sdk" folder) WITHOUT attempting
    /// a pip install. Exists so readiness can be checked cheaply -- EnsureSdk's bounded
    /// install can block for up to two minutes, which is far too slow for a UI probe.
    /// </summary>
    public static (bool Ready, string? PythonPath, string? Error) ProbeSdk(string py)
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

        return (false, null,
            "The muxswarm SDK is not importable by the resolved python. Install it with " +
            "`pip install muxswarm`, set MUX_SDK_PATH to a local mux-swarm-sdk clone, or place " +
            "the SDK under <install-dir>/sdk. Dynamic mode needs it; static mode does not.");
    }

    public static (bool Ready, string? PythonPath, string? Error) EnsureSdk(string py)
    {
        if (ProbeSdk(py) is { Ready: true } hit) return hit;

        // Auto-install from PyPI, best-effort and bounded. uv is tried FIRST because it is
        // what the SDK is distributed with and what mux's own venvs use: uv-managed venvs
        // commonly have NO pip module at all (uv installs without bootstrapping pip), so a
        // raw `python -m pip` fails with "No module named pip" even though the environment
        // is perfectly installable -- and uv installs in ~200ms. Order:
        //   1. `uv pip install --python <py>` -- the fast, primary path (SDK ships with uv).
        //   2. `python -m pip` -- for environments that have pip but not uv.
        //   3. `python -m ensurepip` then pip -- last resort to bootstrap pip.
        // NOTE: <py> is an ABSOLUTE interpreter path (see ResolvePython), so uv installs into
        // the exact interpreter the import-probe and driver launch use -- no cross-interpreter
        // mismatch that would install successfully yet fail the re-probe.
        if (ResolveUv() is { } uv &&
            TryInstall(py, uv, "pip", "install", "--python", py, "muxswarm"))
            return (true, null, null);

        if (TryInstall(py, py, "-m", "pip", "install", "--quiet", "muxswarm")) return (true, null, null);

        if (TryInstall(py, py, "-m", "ensurepip", "--upgrade") &&
            TryInstall(py, py, "-m", "pip", "install", "--quiet", "muxswarm"))
            return (true, null, null);

        return (false, null,
            "[workflow] The muxswarm SDK is not importable by the resolved python and auto-install failed " +
            "(tried pip, uv, and ensurepip). Fix one of: `pip install muxswarm`, `uv pip install muxswarm`, " +
            "set MUX_SDK_PATH to a local mux-swarm-sdk clone, or place the SDK under <install-dir>/sdk. " +
            "Dynamic mode needs it; /workflow static does not.");
    }

    /// <summary>Run an installer command and report whether the SDK imports afterwards. Args are
    /// passed individually via ArgumentList so an embedded path (e.g. uv's --python "&lt;abs path&gt;")
    /// is never re-split or quote-mangled -- the same class of bug that broke ProbeImport.</summary>
    private static bool TryInstall(string py, string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            try { p.StandardInput.Close(); } catch { }
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(120_000)) { try { p.Kill(true); } catch { } return false; }
            if (p.ExitCode != 0) return false;
        }
        catch { return false; }
        // ensurepip installs no package, so re-probing here also gates that step correctly.
        return args.Any(a => a.Contains("ensurepip")) || ProbeImport(py, null);
    }

    /// <summary>Locate the uv launcher, or null when it is not installed.</summary>
    internal static string? ResolveUv()
    {
        var exe = OperatingSystem.IsWindows() ? "uv.exe" : "uv";
        // uv's default install location is not always on a service's PATH.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var cand in new[]
                 {
                     exe,
                     Path.Combine(home, ".local", "bin", exe),
                     Path.Combine(home, ".cargo", "bin", exe),
                 })
        {
            try
            {
                var psi = new ProcessStartInfo(cand, "--version")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                using var p = Process.Start(psi);
                if (p is not null && p.WaitForExit(10_000) && p.ExitCode == 0) return cand;
            }
            catch { /* try the next candidate */ }
        }
        return null;
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
            // Pass args via ArgumentList, NOT a quoted string. `-c "import muxswarm"` as a single
            // raw argument gets its quotes mangled by .NET's argument round trip, so python never
            // sees a valid -c program and drops into the interactive REPL, blocking on stdin until
            // the 15s timeout kills it -- which reads as a FALSE "not importable" and defeats the
            // whole SDK-auto-install ladder (each install's post-probe also hangs). ArgumentList
            // quotes each element correctly; redirecting + closing stdin removes any residual
            // possibility of an interpreter waiting on input.
            var psi = new ProcessStartInfo(py)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import muxswarm");
            if (pythonPath is not null)
            {
                var existing = Environment.GetEnvironmentVariable("PYTHONPATH");
                psi.Environment["PYTHONPATH"] = string.IsNullOrEmpty(existing)
                    ? pythonPath
                    : pythonPath + Path.PathSeparator + existing;
            }
            using var p = Process.Start(psi);
            if (p is null) return false;
            try { p.StandardInput.Close(); } catch { }
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15_000)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Resolve a python launcher to an ABSOLUTE interpreter path, or null when none is on
    /// PATH. Returning the absolute path (via sys.executable) rather than a bare launcher name is
    /// essential: pip, uv (--python), the import probe, and the driver launch must all target the
    /// SAME interpreter. A bare "python.exe" is re-resolved independently by each of those (PATH
    /// order, uv's own discovery, the Windows Store app-execution alias), so uv can install the SDK
    /// into one python while the probe checks another -- yielding a false "auto-install failed" even
    /// though the install succeeded. Canonicalizing here removes that ambiguity entirely.</summary>
    public static string? ResolvePython()
    {
        foreach (var cand in OperatingSystem.IsWindows()
                     ? new[] { "python.exe", "python3.exe", "py.exe" }
                     : new[] { "python3", "python" })
        {
            try
            {
                // Canonicalize to the real interpreter path in one shot. This also runs the
                // candidate, so a non-functional alias is skipped rather than returned.
                // Pass args via ArgumentList, NOT a single quoted string: the inner quotes of
                // `-c "import sys; ..."` get mangled by .NET's arg-string round trip, so python
                // never sees a valid -c program and drops into the interactive REPL, blocking on
                // stdin until it is killed. ArgumentList quotes each element correctly. Also
                // redirect stdin and close it, so even a misparse cannot wait on input.
                var psi = new ProcessStartInfo(cand)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add("import sys; print(sys.executable)");
                using var p = Process.Start(psi);
                if (p is null) continue;
                try { p.StandardInput.Close(); } catch { }
                // Read stdout ASYNC and bound the wait: a bare "python.exe" can also resolve to the
                // Windows Store app-execution stub, which may block without producing output. Drain
                // both pipes async, wait with a hard ceiling, and kill + skip anything that overruns.
                var outTask = p.StandardOutput.ReadToEndAsync();
                _ = p.StandardError.ReadToEndAsync();   // drain so the child can never block on a full stderr pipe
                if (!p.WaitForExit(5000)) { try { p.Kill(true); } catch { } continue; }
                if (p.ExitCode != 0) continue;
                var exe = (outTask.Wait(1000) ? outTask.Result : "").Trim();
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe)) return exe;
            }
            catch { /* try next */ }
        }
        return null;
    }

    /// <summary>The scripting contract + example fed to the model when it authors a driver
    /// script (giga run_dynamic_workflow and /workflow dynamic share it).</summary>
    /// <summary>
    /// Pre-flight guard for a dynamic-workflow launch: verifies the child engine can resolve config
    /// and reach a provider BEFORE the driver is spawned, so a misconfigured/half-staged install
    /// fails fast with a clear reason instead of producing a run full of empty-output tasks. Returns
    /// null when the environment looks healthy, or a human-readable error string to surface instead
    /// of launching. Runs one short headless smoke turn against the first agent named in the script's
    /// manifest (falling back to a configured agent).
    /// </summary>
    private static string? PreflightConfigProbe(string binary, string? cfg, string? swarmcfg, string script)
    {
        // 1) Hard config-existence check. Children inherit MUX_CFG/MUX_SWARMCFG; if absent they drop
        //    into first-run setup and die "No endpoint provided. Setup failed." on every task.
        if (string.IsNullOrEmpty(cfg) || !File.Exists(cfg))
            return $"[workflow] Pre-flight failed: config not found at '{cfg ?? PlatformContext.ConfigPath}'. " +
                   "Child engines can't resolve a provider and every task would return empty. " +
                   "Fix the install's Configs/ (Config.json + Swarm.json) and retry.";
        if (string.IsNullOrEmpty(swarmcfg) || !File.Exists(swarmcfg))
            return $"[workflow] Pre-flight failed: swarm config not found at '{swarmcfg ?? PlatformContext.SwarmPath}'. " +
                   "Fix the install's Configs/Swarm.json and retry.";

        // 2) One fast smoke turn: prove a child can actually reach the provider and stream output.
        //    Cheap insurance (~a few seconds) against a whole pipeline of empty failures.
        string probeAgent = FirstManifestAgent(script) ?? "WebAgent";
        try
        {
            var ppsi = new ProcessStartInfo(binary,
                $"--stdio --agent {probeAgent} \"Reply with the single word: ok\" --cfg \"{cfg}\" --swarmcfg \"{swarmcfg}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                WorkingDirectory = PlatformContext.BaseDirectory,
            };
            using var pp = Process.Start(ppsi);
            if (pp is null) return null;   // can't probe; don't block the launch on our own failure
            try { pp.StandardInput.Close(); } catch { /* already closed */ }
            var stdoutTask = pp.StandardOutput.ReadToEndAsync();
            _ = pp.StandardError.ReadToEndAsync();   // drain stderr so the child can't block on a full pipe
            if (!pp.WaitForExit(45_000))
            {
                try { pp.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return null;   // slow provider is not a hard failure; let the real run proceed
            }
            string outText = stdoutTask.GetAwaiter().GetResult() ?? "";
            // --stdio emits NDJSON. A config/setup failure emits "No endpoint provided" / "Setup failed".
            // Only BLOCK on an explicit provider/setup failure - the dominant, deterministic cause of
            // whole-run empty cascades. A merely stream-less turn (odd agent, terse reply) is NOT
            // treated as fatal, to avoid false positives that would wrongly refuse a healthy launch.
            if (outText.Contains("No endpoint provided") || outText.Contains("Setup failed"))
                return "[workflow] Pre-flight failed: a child engine could not reach a provider " +
                       "(\"No endpoint provided / Setup failed\"). Check Config.json provider/endpoint " +
                       "and retry - launching would otherwise produce a run of empty-output tasks.";
        }
        catch
        {
            // Probe infrastructure failure is not the workflow's fault - don't block the launch.
            return null;
        }
        return null;
    }

    /// <summary>Best-effort extract of the first agent name from a driver script's manifest (the
    /// first <c>"agent": "Name"</c> occurrence). Used to pick a representative pre-flight probe
    /// agent; returns null when none is found.</summary>
    private static string? FirstManifestAgent(string script)
    {
        var m = System.Text.RegularExpressions.Regex.Match(script, "\"agent\"\\s*:\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

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
          (summary of the last task_complete, else the concatenated stream). `res.text` raises
          AttributeError and fails the task.
        - SUCCESS = `res.ok AND (res.final_summary or res.streamed_text).strip()` (MANDATORY). `res.ok`
          alone is NOT enough: a child can exit 0 with NO output yet `res.ok` is still True. Treat
          empty output as a FAILURE.
        - ON FAILURE OR EMPTY (MANDATORY): never leave a black hole. Put `res.exit_code`, joined
          `res.errors`, and any captured child-stderr tail into the failed status "detail" AND write
          them to task_<id>.out, so the /workflows viewer shows a reason when the row is expanded.
        - DEPENDENCY GATING (MANDATORY - no cascading run failures): a section that consumes an
          upstream task’s output MUST check the upstream SUCCEEDED and produced non-empty output
          first. If an upstream is empty/failed, do NOT feed blank text downstream: mark each
          dependent {"status":"skipped","detail":"upstream <id> produced no output"} (or attempt a
          recovery path) and keep going. One failed task must NEVER nuke the whole run - independent
          branches continue, and every failure is surfaced on its own task, not as a wall of blanks.
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
          sections through variables (gated per above).
        - TERMINAL MARKER (MANDATORY): emit the run marker in a try/finally so an abort can never
          leave the run stuck "running". In finally append {"run":"done"} on success, or
          {"run":"failed","error":"<why>"} if a REQUIRED task failed. A run with some skipped/failed
          branches but usable output should still end {"run":"done"} - reserve "failed" for when the
          run’s primary deliverable could not be produced.
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
            def goal_from(upstream, tid="synth"):
                # Build a downstream goal from upstream text; large text auto-routes through a file.
                return goal_arg(tid, "Using this prior output:\n" + upstream + "\n\nNow do: ...")
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
                        out = (res.final_summary or res.streamed_text or "").strip()
                        # SUCCESS = res.ok AND non-empty output. res.ok alone can be True with no output.
                        if not (res.ok and out):
                            why = ("; ".join(res.errors) if res.errors else "empty output") + f" (exit={res.exit_code})"
                            out_write(tid, f"\n[TASK FAILED] {why}\n")   # never a black hole - reason lands in .out
                            emit(task=tid, status="failed", detail=why[:160], secs=int(time.monotonic()-t0))
                            return ""   # signal empty to dependents; do NOT raise (one fail must not nuke the run)
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
                        out_write(tid, f"\n[TASK ERROR] {e}\n")
                        emit(task=tid, status="failed", detail=str(e)[:160], secs=int(time.monotonic()-t0))
                        return ""   # swallow: dependents gate on the empty return, the run continues
            run_ok = True
            try:
                r1 = await run_task("t1", "WebAgent", "research X")
                # DEPENDENCY GATING: only run the dependent if its upstream produced output.
                if r1.strip():
                    await run_task("t2", "DataAnalysisAgent", goal_from(r1))
                else:
                    emit(task="t2", status="skipped", detail="upstream t1 produced no output")
                    run_ok = False   # a REQUIRED deliverable could not be produced
            except Exception as e:
                run_ok = False; emit(run="failed", error=str(e)[:200]); return
            finally:
                # TERMINAL MARKER always emitted (try/finally) so the run can never stick at "running".
                if run_ok: emit(run="done")
                else: emit(run="failed", error="a required task produced no output")
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
            // Redirect stdin too. When serve mode launches the driver, an un-redirected stdin is
            // INHERITED from the serve process -- a pipe/handle with no console attached. CPython's
            // startup (io/site init) can then block on that handle indefinitely: the driver stays
            // alive but never reaches even its first line (manifest.json write), so the run shows
            // 0/0 with an empty driver.log forever. Redirecting + closing stdin gives the child a
            // clean, closed input stream so it starts normally. (The same inherited-stdin hang bit
            // the SDK probe helpers.)
            RedirectStandardInput = true,
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

        // PRE-FLIGHT (v0.13.1): child mux processes MUST be able to resolve config + reach a
        // provider, or every task returns an empty RunResult and the whole run cascades into blank
        // failures (live-diagnosed: a config-less/half-staged install nukes the entire pipeline).
        // Catch it ONCE here, before launching a doomed driver, and surface a clear reason.
        var binForProbe = psi.Environment["MUX_BINARY"]!;
        psi.Environment.TryGetValue("MUX_CFG", out var cfgForProbe);
        psi.Environment.TryGetValue("MUX_SWARMCFG", out var swarmForProbe);
        var preflight = PreflightConfigProbe(binForProbe, cfgForProbe, swarmForProbe, script);
        if (preflight is { } preflightError)
            return preflightError;

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("python did not start");
            // Close the driver's stdin immediately: it never reads input, and leaving the pipe open
            // is what lets a serve-launched child block in interpreter startup.
            try { proc.StandardInput.Close(); } catch { /* already gone */ }
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

    /// <summary>Absolute path to a run's authored driver script, or null when the run/dir is gone.</summary>
    public static string? ScriptPath(string runId)
    {
        var run = WorkflowRunRegistry.Find(runId);
        if (run?.RunDir is null) return null;
        var p = Path.Combine(run.RunDir, "driver.py");
        return File.Exists(p) ? p : null;
    }

    /// <summary>Read a run's driver script, or null when unavailable.</summary>
    public static string? ReadScript(string runId)
        => ScriptPath(runId) is { } p ? File.ReadAllText(p) : null;

    /// <summary>Overwrite a run's driver script (used by the web editor before a re-run). Returns
    /// an error string on failure, else null. Contract-validated so a broken edit is rejected up
    /// front rather than producing a dead run.</summary>
    public static string? WriteScript(string runId, string script)
    {
        if (ScriptPath(runId) is not { } p) return "No script for this run.";
        if (ValidateScript(script) is { } rejection) return rejection;
        try { File.WriteAllText(p, script, new UTF8Encoding(false)); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Read a task's captured output (task_&lt;id&gt;.out) for a run, or null.</summary>
    public static string? ReadTaskOutput(string runId, string taskId)
    {
        var run = WorkflowRunRegistry.Find(runId);
        if (run?.RunDir is null) return null;
        // taskId is caller-supplied; keep it to a bare file stem so it can never traverse.
        var safe = new string(taskId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (safe.Length == 0) return null;
        var p = Path.Combine(run.RunDir, $"task_{safe}.out");
        return File.Exists(p) ? File.ReadAllText(p) : null;
    }

    /// <summary>Persist a run's driver script as a reusable saved definition
    /// (&lt;name&gt;.workflow.json in the Teams dir, shape { name, mode:"dynamic", script }). Returns
    /// (savedName, error). Shared by the web API and the TUI /workflows save verb.</summary>
    public static (string? Saved, string? Error) SaveRunAsDefinition(string runId, string? asName = null)
    {
        var run = WorkflowRunRegistry.Find(runId);
        if (run is null) return (null, $"No run '{runId}'.");
        if (ReadScript(runId) is not { } script) return (null, "This run has no driver script to save.");
        var name = SanitizeName(string.IsNullOrWhiteSpace(asName) ? run.Name : asName!);
        try
        {
            Directory.CreateDirectory(PlatformContext.TeamsDirectory);
            var file = Path.Combine(PlatformContext.TeamsDirectory, $"{name}.workflow.json");
            var payload = JsonSerializer.Serialize(new { name, mode = "dynamic", script },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(file, payload, new UTF8Encoding(false));
            return (name, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    /// <summary>Re-launch a run from its (optionally edited) driver script without re-authoring.
    /// Returns the launch message (which the caller inspects for success like the initial POST).</summary>
    public static string RerunFromRun(string runId)
    {
        var run = WorkflowRunRegistry.Find(runId);
        if (run is null) return $"[workflow] No run '{runId}'.";
        if (ReadScript(runId) is not { } script) return "[workflow] This run has no driver script to re-run.";
        return Launch(run.Name, script, ResolvePython());
    }

    /// <summary>Launch a previously saved dynamic definition (&lt;name&gt;.workflow.json with a
    /// "script" field). Returns the launch message. Saved STATIC step-workflows are not dynamic and
    /// are handled by the existing static path, so this only accepts definitions carrying a script.</summary>
    public static string LaunchSaved(string savedName)
    {
        var name = SanitizeName(savedName);
        var file = Path.Combine(PlatformContext.TeamsDirectory, $"{name}.workflow.json");
        if (!File.Exists(file)) return $"[workflow] No saved workflow '{name}'.";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("script", out var sc) || sc.ValueKind != JsonValueKind.String)
                return $"[workflow] Saved workflow '{name}' has no dynamic script (it may be a static step workflow).";
            return Launch(name, sc.GetString()!, ResolvePython());
        }
        catch (Exception ex) { return $"[workflow] Could not load saved workflow '{name}': {ex.Message}"; }
    }

    /// <summary>Delete a saved dynamic definition (&lt;name&gt;.workflow.json). Returns (deleted, error).</summary>
    public static (bool Deleted, string? Error) DeleteSaved(string savedName)
    {
        var name = SanitizeName(savedName);
        var file = Path.Combine(PlatformContext.TeamsDirectory, $"{name}.workflow.json");
        if (!File.Exists(file)) return (false, $"No saved workflow '{name}'.");
        try { File.Delete(file); return (true, null); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>Reduce an arbitrary label to a safe file stem for a saved definition.</summary>
    private static string SanitizeName(string raw)
    {
        var name = new string((raw ?? "").Trim()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray())
            .Trim('-');
        if (name.Length == 0) name = "workflow";
        return name.Length > 48 ? name[..48] : name;
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
