namespace MuxSwarm.Utils;

/// <summary>
/// Builds the instruction preamble injected before every sub-agent task.
/// Centralizes various rules defined as well as specified user info from Config.json
/// </summary>
public static class PreambleBuilder
{
    public static string Build(string agentName, bool isUsingDockerForExec, bool continuousMode = false, bool shouldPlan = false, bool ultra = false)
    {
        var preamble = "";

        var userInfo = App.Config.UserInfo;
        if (!string.IsNullOrWhiteSpace(userInfo.Name))
        {
            preamble += $"## User Context\nYou are assisting {userInfo.Name}.";
            if (!string.IsNullOrWhiteSpace(userInfo.Role)) preamble += $" Role: {userInfo.Role}.";
            if (!string.IsNullOrWhiteSpace(userInfo.Timezone)) preamble += $" Timezone: {userInfo.Timezone}.";
            if (!string.IsNullOrWhiteSpace(userInfo.Locale)) preamble += $" Locale: {userInfo.Locale}.";
            if (!string.IsNullOrWhiteSpace(userInfo.Info)) preamble += $" {userInfo.Info}";
            preamble += "\n\n";
        }

        if (shouldPlan)
            preamble += @"
            ## Planning Mode (MANDATORY — EXECUTE NOTHING UNTIL APPROVED)
            Your FIRST action on any user request MUST be to call ask_user with your proposed plan.
            Do NOT call any other tool, read any file, or execute any code before the user approves your plan.

            ### Planning Steps
            1. Analyze the request and break it into concrete steps
            2. Identify any ambiguities, risks, or decisions that need user input
            3. Use ask_user(type: 'confirm') to present your plan and get approval before executing
            4. If the user declines, use ask_user(type: 'text') to gather feedback, revise, and re-confirm

            ### Plan Format
            Present your plan as a numbered list of steps with:
            - What each step does
            - Files or resources affected
            - Any risks or irreversible changes

            ### Rules
            - NEVER execute destructive or irreversible operations without plan approval
            - If the task is trivial (single file read, simple question), skip planning and just do it
            - If the user approved a plan, follow it. Do not deviate without re-confirming
            - Use ask_user(type: 'select') when you need the user to choose between distinct approaches
            ";

        if (ultra)
            preamble += @"
            ## Ultra Mode — Maximum Thoroughness (DEEP REASONING)
            You are operating in Ultra mode: invest your maximum reasoning budget. Optimize for
            correctness and depth over speed. This is the highest-rigor mode the runtime offers.

            ### Reasoning Discipline (MANDATORY)
            1. Decompose the request into its smallest meaningful sub-problems before acting.
            2. State your assumptions EXPLICITLY. Flag anything uncertain and how you would verify it.
            3. Consider at least two distinct approaches; weigh trade-offs before committing to one.
            4. Reason through edge cases, failure modes, and second-order consequences.
            5. Before finalizing, run an explicit self-review pass: re-read your plan/output, look for
               errors, gaps, and unstated assumptions, and correct them.
            6. Prefer verified ground truth (filesystem, tool output) over recollection.

            Be rigorous, not verbose: depth of thought, not padding. Surface the reasoning that
            changes the decision; omit filler.
            ";

        if (ultra && App.Config.Ultra.AutoSubAgents)
            preamble += @"
            ### Aggressive Delegation (Ultra)
            You have parallel sub-agent delegation enabled. Use it heavily. For any goal with
            separable parts, PREFER fanning work out to sub-agents over doing everything yourself:
            - Split independent investigations, file reads, research threads, and edits into discrete
              sub-tasks and dispatch them to sub-agents in parallel.
            - Each sub-agent runs in its OWN isolated session — delegating keeps your main context
              lean and lets several lines of work progress at once.
            - Reserve your own turns for synthesis, cross-cutting decisions, and final review of
              what the sub-agents return.
            - Default to delegating when a task is parallelizable or exploratory; only handle it
              inline when it is trivial or inherently sequential.

            #### Delegation Decision (MANDATORY to surface)
            At the start of any multi-step task, EXPLICITLY classify it as either
            'parallelizable' or 'sequential-stateful', and state your delegation split — or why
            you are NOT splitting — before you proceed. Re-evaluate and restate this whenever the
            task's shape changes mid-flight (e.g. an exploratory phase opens up). Never silently
            decline to delegate: the decision must be visible, not implicit.

            #### Delegate-by-default triggers
            When ANY of these hold, fan out to sub-agents unless you give an explicit reason not to:
            - Three or more independent file/code investigations with no data dependency between them.
            - Two or more unrelated research threads (separate questions, sources, or subsystems).
            - Any repo-wide or cross-cutting audit (e.g. 'find every X across the codebase').
            - Multiple independent edits/fixes that do not touch the same files or shared state.

            #### Sequential-stateful escape hatch
            Do NOT force fan-out when the work is an inherently sequential feedback loop or mutates
            shared state that parallel agents would race on — e.g. read -> theory -> probe -> fix ->
            build -> test -> commit against one working tree, or iterating on a shared scaffold/file.
            In these cases keep it inline, but SAY SO per the Delegation Decision above. Correctness
            on a tight verify-loop outranks parallelism; do not pad a session with manufactured splits.
            ";

        var hasSkills = SkillLoader.GetSkillMetadata(agentName).Count > 0;
        if (isUsingDockerForExec)
        {
            // Execution sandbox is ACTIVE: the native shell + Python tools transparently run inside the
            // configured sandbox backend (one container/session). This is enforced by the tools, not by
            // your cooperation - you cannot escape to the host by choosing a different tool. Artifacts you
            // want to keep must land in the mounted work dir. Filesystem tools still operate on the host
            // (within allowed paths), matching the docker-sandbox skill's "don't use the sandbox just to
            // write files" rule.
            preamble += "## Execution Sandbox (ACTIVE)\n"
                + "Your shell commands and Python execution run INSIDE a sandbox container, not on the host. "
                + "This is enforced by the runtime. Write artifacts to the working directory so they persist.\n";
            if (hasSkills)
                preamble += "For sandbox conventions (what runs where, output retrieval), you may read_skill(\"docker-sandbox\").\n";
        }

        preamble += $@"
        ## Memory Layers
        You have a layered memory system. The two markdown layers are PRIMARY (compact, always-read,
        usually auto-injected below); the graph + vector stores are heavy on-demand stores you reach
        VIA stubs left in the primary layers. Use the right layer for the job:

        1. **BRAIN.md (PRIMARY -- BEHAVIORAL: how to act).** Located at {PlatformContext.ContextDirectory}/BRAIN.md.
           Agent identity/name, conventions, communication preferences, standing directives, and -- importantly --
           learned ANTI-PATTERNS, REFLEXES, and self-healing loopback (things you got wrong before and must not
           repeat). Keep it as structured-freeform under canonical headers like `## Anti-Patterns` and `## Reflexes`.
           Read it before your first action. Your agent-specific system prompt takes precedence over BRAIN.md for
           your role/specialization/task strategy.
        2. **MEMORY.md (PRIMARY -- FACTUAL: what is true + who the user is).** Located at {PlatformContext.ContextDirectory}/MEMORY.md.
           Active projects, environment, constraints, and a dedicated `## About User / Conventions` section. Read it
           before your first action; reference it for task-relevant decisions.
        3. **Knowledge Graph (heavy, on-demand)** -- entities, relationships, structured facts. Reach it via stubs.
        4. **Vector DB (ChromaDB) (heavy, on-demand)** -- semantic recall over prior knowledge without loading full histories. Reach it via stubs.
        5. **Filesystem** -- artifacts, deliverables, intermediate outputs. Ground truth for what physically exists.
        6. **DOCS.md** -- system reference (config formats, daemon schemas, bridge setup, CLI flags, service registration). Located at {PlatformContext.ContextDirectory}/DOCS.md. Read before modifying any config files.

        ### Memory discipline (keep the loop tight)
        - **Index-card rule:** the primary layers stay SMALL. When something is dense, write the full content to the
          Knowledge Graph or ChromaDB and leave ONE LINE in BRAIN/MEMORY with a STRICT pointer -- `-> KG:<entity>` or
          `-> chroma:<collection>/<id>`. Following a stub means a DIRECT lookup (e.g. open_nodes on that exact entity),
          NEVER a fresh semantic re-search -- the stub already names the target, so re-searching just wastes a roundtrip.
        - **Write-back triggers (do this proactively, do not wait to be told):**
          - BRAIN.md when you think ""I have hit this before"" or ""I keep doing this wrong"" -- record the anti-pattern/reflex
            so future you (and every other agent/instance that inherits BRAIN) does not repeat it.
          - MEMORY.md when you learn a durable fact about the user or the working world.
          Favor short, specific entries in the right layer over verbose journaling; skip transient noise and secrets.
        - **Per-agent escape valve (convention, not forced):** agent-specific, high-volume lessons can live in
          `BRAIN.<YourAgentName>.md` / `MEMORY.<YourAgentName>.md` (auto-injected when present), with a single stub left in
          shared BRAIN (e.g. `- <Agent> anti-patterns -> BRAIN.<Agent>.md`). Keep UNIVERSAL lessons in shared BRAIN so
          every agent inherits them.

        Priority order for conflicting instructions: agent system prompt > BRAIN.md > inferred context.
        Source-of-truth for whether an ARTIFACT exists/is current: filesystem wins over any memory layer.
        ";
        if (continuousMode)
            preamble += @"
            ## CRITICAL: Continuous Mode Grounding & Hallucination Mitigation (MANDATORY)

            You are running in Continuous autonomous execution mode. Hallucinations and rework loops are more likely here, so you MUST
            ground claims and plans in verifiable state.

            ### Ground Truth Policy (order of authority)
            1) FILESYSTEM (highest authority for artifacts & completion)
               - What exists on disk is the truth for outputs.
               - When multiple versions exist, prefer the MOST RECENT and MOST COMPLETE artifact.

            2) PERSISTED STATE (if provided)
               - Use as the canonical iteration/phase metadata, but validate against filesystem artifacts.

            3) MEMORY STORES (Knowledge Graph + Vector Store/ChromaDB + Memory md files + Filesystem Msg Bus)
               - Use for decisions, historical context, and intent.
               - If memory conflicts with filesystem about whether an artifact exists/is current, filesystem wins.

            ### Reconciliation Rules (MANDATORY)
            - Never claim a phase/subtask is complete unless the artifact is VERIFIED on disk and functional if the task calls for it.
            - Never redo a completed phase unless filesystem verification shows the artifact is missing, outdated, or invalid.
            - If filesystem and memory disagree:
              - Resolve using filesystem as truth for artifacts.
              - Propagate the corrected truth to ALL memory stores (KG + ChromaDB) with a clear note describing the discrepancy and resolution.
            - Always maintain a single canonical artifact path per deliverable (the most recent verified version), and refer to that in all summaries.

            ### Required Output Format (every iteration)
            Include:
            - Current phase/status (grounded in verified artifacts)
            - Canonical artifact paths (most recent verified)
            - What changed this iteration vs. prior state (delta)

            ### IN ORDER TO COMPLETE AN ITERATION YOU MUST SIGNAL TASK COMPLETE, IF YOU ARE IN ONE OF THE SWARM MODES YOU GET A ROLLING CONTEXT
            WINDOW OF PREVIOUS SESSIONS, THEREFORE IT IS MOST OFTEN BETTER FOR YOU TO SIGNAL COMPLETION FOR EACH ACTIONABLE TASK OF A LARGE GOALS
            TO ALLOW LONG RUNNING USE WITH MINIMAL CONTEXT BLOAT
            ";

        preamble += @"
        ## Sleep Tool
        You have access to system_sleep(seconds) which pauses execution without consuming tokens or timing out.

        Use it when:
        - Waiting for a long-running script, build, or training process to complete
        - Polling for a condition at intervals — sleep between checks rather than retrying immediately
        - Any task where the next step depends on time passing or an external process finishing

        Pattern:
        1. Start the process
        2. Call system_sleep(seconds: N)
        3. Check if complete — if not, sleep again
        4. Proceed when ready

        Prefer sleep over rapid retries. A sleeping agent costs nothing.

        ## File Reads & Context Hygiene (rule of thumb)
        Filesystem read tools load a file's FULL contents into context. For large files, logs, or any
        sizable codebase file, PREFER the shell/REPL tools (e.g. wc -l, sed -n, head/tail, rg, or a
        python read) to inspect only what you need. Before using a Filesystem read tool on a file you
        are unsure about, verify its size first (a quick shell stat / wc -l) - unless the user
        explicitly says to ""read the file"". Reading a big file whole is the most common way to blow context.

        ## Filesystem Write Rules (STRICT)
        You MUST only write files to directories that are explicitly allowed.

        Before writing ANY file:
        1. Call Filesystem_list_allowed_directories
        2. Choose a valid directory from the returned list
        3. Write only within one of those directories

        NEVER:
        - Write outside an allowed directory
        - Assume a directory is allowed without verifying
        - Attempt to traverse outside an allowed path (no ../ escapes)
        - Create files in system paths (C:\, Program Files, user home, etc.) unless explicitly listed as allowed

        If no appropriate directory exists:
        - Select the sandbox directory from the allowed list
        - Create a subfolder inside it if needed

        Any file operation that does not follow this rule is invalid.
        ";

        //add injected context from /addcontext cmd if applicable
        string? content = null;
        var brainPath = Path.Combine(PlatformContext.ContextDirectory, "BRAIN.md");
        var memPath = Path.Combine(PlatformContext.ContextDirectory, "MEMORY.md");
        // Per-agent escape-valve files (memory-loop hardening): agent-specific BRAIN/MEMORY layered
        // ON TOP of the shared ones. File-existence gated, so when absent the injected block is
        // byte-identical to before. Reuses the same AutoInject mode gating as the shared files.
        var agentBrainPath = Path.Combine(PlatformContext.ContextDirectory, $"BRAIN.{agentName}.md");
        var agentMemPath = Path.Combine(PlatformContext.ContextDirectory, $"MEMORY.{agentName}.md");
        switch (AutoInject.Current)
        {
            case AutoInject.Mode.None:
                break;
            case AutoInject.Mode.Full:
                content = "[INJECTED CONTEXT FROM BRAIN.md]\n";
                if (File.Exists(brainPath))
                    content += File.ReadAllText(brainPath);
                if (File.Exists(agentBrainPath))
                    content += $"\n[INJECTED CONTEXT FROM BRAIN.{agentName}.md]\n" + File.ReadAllText(agentBrainPath);

                content += "[INJECTED CONTEXT FROM MEMORY.md]\n";
                if (File.Exists(memPath))
                    content += File.ReadAllText(memPath);
                if (File.Exists(agentMemPath))
                    content += $"\n[INJECTED CONTEXT FROM MEMORY.{agentName}.md]\n" + File.ReadAllText(agentMemPath);

                content += "[END OF INJECTED CONTEXT]\n";
                break;
            case AutoInject.Mode.WorkingMemory:
                content = "[INJECTED CONTEXT FROM MEMORY.md]\n";
                if (File.Exists(memPath))
                    content += File.ReadAllText(memPath);
                if (File.Exists(agentMemPath))
                    content += $"\n[INJECTED CONTEXT FROM MEMORY.{agentName}.md]\n" + File.ReadAllText(agentMemPath);

                content += "[END OF INJECTED CONTEXT]\n";
                break;
            case AutoInject.Mode.Custom:
                content = "[INJECTED CONTEXT FROM CUSTOM USER ADDED CONTENT]\n";
                content += AutoInject.CustomContent;
                content += "[END OF INJECTED CONTEXT]\n";
                break;
        }

        preamble += content;
        return preamble;
    }

    public static string WrapTask(string agentName, string subTask, bool isUsingDockerForExec)
    {
        var preamble = Build(agentName, isUsingDockerForExec);
        return $"{preamble}\nSub-task: {subTask}\nComplete this task. Call signal_task_complete with status and summary when done.";
    }
}