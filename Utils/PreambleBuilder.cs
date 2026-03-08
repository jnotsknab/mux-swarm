namespace MuxSwarm.Utils;

/// <summary>
/// Builds the instruction preamble injected before every sub-agent task.
/// Centralizes the skill, sleep, and filesystem write rules.
/// </summary>
public static class PreambleBuilder
{
    public static string Build(string agentName, bool isUsingDockerForExec, bool continuousMode = false)
    {
        var preamble = "";

        var hasSkills = SkillLoader.GetSkillMetadata(agentName).Count > 0;
        if (hasSkills && isUsingDockerForExec)
        {
            preamble = "CRITICAL: You MUST call read_skill(\"docker-sandbox\") as your VERY FIRST action. "
                + "No exceptions. Do not check directories or execute any code before reading docker-sandbox. "
                + "After reading docker-sandbox, call list_skills to discover other available skills and read any that match your task.\n";
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