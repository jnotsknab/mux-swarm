using Microsoft.Extensions.AI;

namespace MuxSwarm.Utils;

public static class LocalAiFunctions
{
    //TODO: Convert to get to methods
    public static AIFunction ListSkillsTool = null!;
    public static AIFunction ReadSkillTool = null!;
    public static AIFunction SleepTool = null!;

    static LocalAiFunctions()
    {
        CreateSkillFuncs();
    }

    private static void CreateSkillFuncs()
    {
        ListSkillsTool = AIFunctionFactory.Create(
            method: () =>
            {
                var skills = SkillLoader.GetSkillMetadata();
                return string.Join("\n", skills.Select(s => $"- {s.Name}: {s.Description}"));
            },
            name: "list_skills",
            description: "List all available skills with their descriptions. Call this first to discover what skills are available before calling read_skill."
        );

        ReadSkillTool = AIFunctionFactory.Create(
            method: (
                [System.ComponentModel.Description("Name of the skill to load. Call list_skills first if you are unsure of available skill names.")]
                string skillName
            ) =>
            {
                var content = SkillLoader.ReadSkill(skillName);
                if (content != null)
                    return content;

                var available = SkillLoader.GetSkillMetadata();
                var listing = string.Join("\n", available.Select(s => $"- {s.Name}: {s.Description}"));
                return $"Skill '{skillName}' not found. Here are the currently available skills — call read_skill again with a valid name:\n{listing}";
            },
            name: "read_skill",
            description: "Read the full instructions for a skill by name. Call list_skills first to discover available skills. " +
                         "Read the relevant skill BEFORE starting a task to follow its best practices."
        );

        SleepTool = AIFunctionFactory.Create(
            method: async (
                [System.ComponentModel.Description("Seconds to pause.")]
                int seconds
                ) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                return $"Slept for {seconds} seconds.";
            },
            name: "system_sleep",
            description: "Pause execution for N minutes without consuming tokens. Use between polling cycles, while waiting for long-running processes, or for scheduled intervals."
        );
    }
}