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
}