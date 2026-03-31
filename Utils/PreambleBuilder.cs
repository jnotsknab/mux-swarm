namespace MuxSwarm.Utils;

/// <summary>
/// Builds the instruction preamble injected before every sub-agent task.
/// Centralizes various rules defined as well as specified user info from Config.json
/// </summary>
public static class PreambleBuilder
{
    public static string Build(string agentName, bool isUsingDockerForExec, bool continuousMode = false, bool shouldPlan = false)
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

        var hasSkills = SkillLoader.GetSkillMetadata(agentName).Count > 0;
        if (hasSkills && isUsingDockerForExec)
        {
            preamble = "CRITICAL: You MUST call read_skill(\"docker-sandbox\") as your VERY FIRST action. "
                + "No exceptions. Do not check directories or execute any code before reading docker-sandbox. "
                + "After reading docker-sandbox, call list_skills to discover other available skills and read any that match your task.\n"
                +  "NOTE: YOU HAVE 4 MEMORY LAYERS — see MEMORY.md system reference above for details.";
        }

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

            3) MEMORY STORES (Knowledge Graph + Vector Store/ChromaDB)
               - Use for decisions, historical context, and intent.
               - If memory conflicts with filesystem about whether an artifact exists/is current, filesystem wins.

            ### Reconciliation Rules (MANDATORY)
            - Never claim a phase/subtask is complete unless the artifact is VERIFIED on disk.
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

        // Load base MEMORY.md for agent system reference
        var memoryMdPath = Path.Combine(PlatformContext.BaseDirectory, "Runtime", "MEMORY.md");
        if (File.Exists(memoryMdPath))
        {
            var memoryContent = File.ReadAllText(memoryMdPath);
            preamble += "## System Reference (MEMORY.md)\n" + memoryContent + "\n\n";
        }

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


        return preamble;
    }

    public static string WrapTask(string agentName, string subTask, bool isUsingDockerForExec)
    {
        var preamble = Build(agentName, isUsingDockerForExec);
        return $"{preamble}\nSub-task: {subTask}\nComplete this task. Call signal_task_complete with status and summary when done.";
    }
}