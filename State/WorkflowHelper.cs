using System.Text.Json;
using MuxSwarm.Utils;

namespace MuxSwarm.State;

public static class WorkflowHelper
{
    public static Workflow Load(string path)
    {
        path = path.Trim('"', '\'', ' ');
    
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Workflow>(json, new JsonSerializerOptions 
        { 
            PropertyNameCaseInsensitive = true 
        }) ?? throw new InvalidOperationException($"Failed to parse workflow: {path}");
    }

    public static void RunWorkflow(Workflow wf)
    {
        string input = string.Join(Environment.NewLine, wf.Steps);
        MuxConsole.InputOverride = new StringReader(input);
    }
}