namespace MuxSwarm.Engine;

public static partial class MuxConsole
{
    /// <summary>Native settings view is available only for docked, interactive input; scripted/stdio retain their prompts.</summary>
    internal static bool CanUseSettingsPicker => ViaDriver && InputOverride == Console.In;

    /// <summary>Return handled/cancel separately from unavailable; no config mutation occurs in the modal.</summary>
    /// <summary>Open the <c>/keybinds</c> panel. False when unavailable (stdio / classic / another modal).</summary>
    internal static bool TryKeybindsPanel(Tui.KeybindsPanelView view, out bool saved)
    {
        saved = false;
        return CanUseSettingsPicker && _driver!.RunKeybindsPanel(view, out saved);
    }

    internal static bool TrySettingsPicker(Tui.SettingsPickerView view, out Tui.TuiConfigCommands.SettingSelection? selection)
    {
        selection = null;
        return CanUseSettingsPicker && _driver!.RunSettingsPicker(view, out selection);
    }
}
