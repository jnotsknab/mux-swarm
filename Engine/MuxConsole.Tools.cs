using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Engine;

public static partial class MuxConsole
{
    /// <summary>Populate the active driver's preview with metadata from this exact caller scope.</summary>
    internal static void SetToolBrowserCatalog(ToolCatalog.Snapshot catalog)
    {
        if (!TuiActive) return;
        lock (ConsoleLock) _driver!.SetToolsCatalog(catalog);
    }

    /// <summary>Render grouped tool metadata; selecting/listing a tool never invokes its function.</summary>
    internal static void WriteToolsCatalog(string? query, ToolCatalog.Snapshot catalog)
    {
        int width = ViaDriver ? _driver!.Width : Math.Max(20, Spectre.Console.AnsiConsole.Profile.Width);
        var rows = TuiComponents.ToolsListing(query, catalog, width);
        WritePanelMarkup("Tools", rows, string.Join("\n", rows.Select(TuiMarkup.Plain)));
    }
}
