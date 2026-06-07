using System.Text.Json;
using MuxSwarm.Utils;

namespace MuxSwarm.State;

public static class WorkflowHelper
{
    public static Workflow Load(string path)
    {
        try
        {
            path = path.Trim('"', '\'', ' ');

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Workflow>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidOperationException($"Failed to parse workflow: {path}");
        }
        catch (FileNotFoundException _)
        {
            MuxConsole.WriteWarning($"Workflow file not found at, {path}");
            return new Workflow();
        }
        catch (InvalidOperationException)
        {
            MuxConsole.WriteWarning($"Workflow file not valid schema, {path}");
            return new Workflow();
        }
    }

    public static void RunWorkflow(Workflow wf)
    {
        string input = string.Join(Environment.NewLine, wf.Steps);
        MuxConsole.InputOverride = new StringReader(input);
    }
}