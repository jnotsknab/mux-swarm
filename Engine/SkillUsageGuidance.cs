namespace MuxSwarm.Engine;

/// <summary>Shared skill-tool instructions: discover only when needed and reuse the current context.</summary>
internal static class SkillUsageGuidance
{
    /// <summary>Conditional discovery policy shared by names-only and descriptive listing tools.</summary>
    internal const string ListDescription =
        "Call list_skills only if the task may need a skill and the relevant skill name is not already in context. " +
        "Reuse known names; do not repeat the catalog on every turn or task. " +
        "If the name is known, call read_skill directly without listing first. ";

    /// <summary>Direct named lookup and definition reuse, with refresh only when necessary.</summary>
    internal const string ReadDescription =
        "Read a relevant skill's full instructions directly by name; list_skills is not a prerequisite when the name is known. " +
        "Reuse a definition already in context; reload only if it is missing (e.g. after compaction), changed, or an explicit refresh is requested. " +
        "Follow relevant skill guidance before doing that work. Skip skill tools when no skill is relevant.";

    /// <summary>A skill name from context is sufficient to load its definition without rediscovery.</summary>
    internal const string SkillNameDescription =
        "Name of the relevant skill to load. Use a name already in context directly; discover names only when unknown.";

    /// <summary>Retries should reconsider relevant guidance without reflexively listing skills again.</summary>
    internal const string RetryHint =
        "Review what went wrong and try a different approach. Reuse relevant skill instructions already in context; " +
        "if more guidance is needed, read_skill by a known name and list_skills only when the name is unknown.";
}
