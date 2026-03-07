using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using MuxSwarm.Setup;
using static MuxSwarm.Setup.Setup;

namespace MuxSwarm.Utils;

public static class CliCmdUtils
{
    public static void ShowKnowledgeGraph(Dictionary<string,McpClient> mcpClients, IList<McpClientTool>? mcpTools = null)
    {
        MuxConsole.WithSpinner("Fetching knowledge graph...", () =>
        {
            Task.Run(async () =>
            {
                var memoryClient = mcpClients.GetValueOrDefault("Memory");
                var readGraphTool = mcpTools?.FirstOrDefault(t => t.Name == "Memory_read_graph");

                if (readGraphTool != null && memoryClient != null)
                {
                    try
                    {
                        var result = await memoryClient.CallToolAsync(
                            readGraphTool.Name.Replace("Memory_", ""),
                            new Dictionary<string, object>()!
                        );
                        var text = string.Join("\n", result.Content
                            .OfType<TextContentBlock>()
                            .Select(b => b.Text));
                        MuxConsole.WritePanel("Knowledge Graph", text);
                    }
                    catch (Exception ex)
                    {
                        MuxConsole.WriteError($"Error reading memory: {ex.Message}");
                    }
                }
                else
                {
                    MuxConsole.WriteWarning("Memory client or tool not found.");
                }
            }).Wait();
        });
    }

    public static void HandleDockerExec(string cfgPath)
    {
        App.Config.IsUsingDockerForExec = ! App.Config.IsUsingDockerForExec;
        MuxConsole.WriteInfo($"Docker Exec is now: {App.Config.IsUsingDockerForExec}");

        MuxConsole.WriteMuted(App.Config.IsUsingDockerForExec
            ? "Agents will route script execution, Python, and git operations through Docker containers. File I/O still uses Filesystem MCP directly."
            : "Agents will execute natively on the host. Docker sandbox is disabled.");

        Common.SaveConfig(App.Config);
        App.Config = LoadConfig(cfgPath);
        SwarmDefaults.PatchPromptPaths(App.Config);
    }
    
    public static void ShowLoadedSkills()
    {
        var skills = SkillLoader.GetSkillMetadata();
        if (skills.Count == 0)
        {
            MuxConsole.WriteWarning("No skills loaded.");
            MuxConsole.WriteMuted($"Skills directory: {PlatformContext.SkillsDirectory}");
            return;
        }

        var text = string.Join("\n", skills.Select(s => $"  {s.Name} — {s.Description}"));
        MuxConsole.WritePanel($"Skills ({skills.Count})", text);
    }
    
    public static void ListSessions()
    {
        var sessionsDir = PlatformContext.SessionsDirectory;

        if (!Directory.Exists(sessionsDir))
        {
            MuxConsole.WriteWarning("No sessions directory found.");
            MuxConsole.WriteMuted($"Expected: {sessionsDir}");
            return;
        }

        var sessionDirs = Directory.GetDirectories(sessionsDir)
            .OrderByDescending(d => d)
            .ToList();

        if (sessionDirs.Count == 0)
        {
            MuxConsole.WriteWarning("No sessions found.");
            return;
        }

        var lines = new List<string>();

        foreach (var dir in sessionDirs)
        {
            string name = Path.GetFileName(dir);
            int fileCount = Directory.GetFiles(dir, "*_session.json", SearchOption.AllDirectories).Length;
            string type = fileCount > 1 ? "swarm" : "single";
            lines.Add($"  {name}  ({type}, {fileCount} agent{(fileCount != 1 ? "s" : "")})");
        }

        var text = string.Join("\n", lines);
        MuxConsole.WritePanel($"Sessions ({sessionDirs.Count})", text);
        MuxConsole.WriteMuted("Use /report <id> to generate a full audit report.");
    }
    
    public static void ReloadSkills()
    {
        MuxConsole.WithSpinner("Reloading skills...", () =>
        {
            SkillLoader.LoadSkills();
        });

        var skills = SkillLoader.GetSkillMetadata();
        if (skills.Count == 0)
        {
            MuxConsole.WriteWarning("No skills found after reload.");
            MuxConsole.WriteMuted($"Skills directory: {PlatformContext.SkillsDirectory}");
        }
        else
        {
            MuxConsole.WriteSuccess($"Reloaded {skills.Count} skills.");
        }
    }
    
    public static async Task ReloadMcpServersAsync(
        Func<AppConfig, Task<bool>> initMcpServers,
        string configPath)
    {
        MuxConsole.WriteInfo("Reloading MCP servers...");

        App.Config = LoadConfig(configPath);

        bool success = await initMcpServers(App.Config);

        if (success)
            MuxConsole.WriteSuccess("MCP servers reloaded.");
        else
            MuxConsole.WriteError("One or more MCP servers failed to reconnect.");
    }
    
    public static void GenerateSessionReports(string? sessionId = null)
    {
        var sessionsDir = PlatformContext.SessionsDirectory;

        if (!Directory.Exists(sessionsDir))
        {
            MuxConsole.WriteWarning("No sessions directory found.");
            MuxConsole.WriteMuted($"Expected: {sessionsDir}");
            return;
        }

        var reportsDir = Path.Combine(PlatformContext.BaseDirectory, "Reports");
        Directory.CreateDirectory(reportsDir);

        List<string> sessionDirs;

        if (!string.IsNullOrEmpty(sessionId))
        {
            var target = Directory.GetDirectories(sessionsDir)
                .FirstOrDefault(d => Path.GetFileName(d)
                    .Equals(sessionId, StringComparison.OrdinalIgnoreCase));

            if (target == null)
            {
                MuxConsole.WriteWarning($"Session '{sessionId}' not found.");
                MuxConsole.WriteMuted($"Check: {sessionsDir}");
                return;
            }

            sessionDirs = [target];
        }
        else
        {
            sessionDirs = Directory.GetDirectories(sessionsDir)
                .OrderByDescending(d => d)
                .ToList();
        }

        if (sessionDirs.Count == 0)
        {
            MuxConsole.WriteWarning("No sessions found.");
            return;
        }

        int generated = 0;

        MuxConsole.WithSpinner($"Generating reports for {sessionDirs.Count} session(s)...", () =>
        {
            foreach (var dir in sessionDirs)
            {
                try
                {
                    string report = SessionSummarizer.GenerateDetailedReport(dir);
                    if (string.IsNullOrWhiteSpace(report)) continue;

                    string fileName = $"{Path.GetFileName(dir)}.md";
                    string outputPath = Path.Combine(reportsDir, fileName);
                    File.WriteAllText(outputPath, report);
                    generated++;
                }
                catch (Exception ex)
                {
                    MuxConsole.WriteWarning($"Failed to generate report for {Path.GetFileName(dir)}: {ex.Message}");
                }
            }
        });

        if (generated == 0)
        {
            MuxConsole.WriteWarning("No reports generated — sessions may be empty.");
        }
        else
        {
            MuxConsole.WriteSuccess($"Generated {generated} report(s) in {reportsDir}");
        }
    }
}