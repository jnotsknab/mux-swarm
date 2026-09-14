namespace MuxSwarm.Engine;

public static partial class MuxConsole
{
    /// <summary>Offer the searchable docked agent picker, returning null selection on cancel.
    /// False preserves the existing classic/stdio/scripted number-or-name prompt.</summary>
    internal static bool TryAgentPicker(IReadOnlyList<Common.AgentDefinition> agents, string? currentName,
        out Common.AgentDefinition? selected)
    {
        selected = null;
        if (!ViaDriver || InputOverride != Console.In) return false;
        return _driver!.RunAgentPicker(new Tui.AgentPickerView(agents, currentName), out selected);
    }
}
