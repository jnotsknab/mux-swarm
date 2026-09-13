namespace MuxSwarm.Engine;

public static partial class MuxConsole
{
    /// <summary>Open the docked TUI model/effort view. Cancelling is handled without a config write;
    /// false lets non-docked/classic/stdio callers retain the existing prompt flow.</summary>
    internal static bool TryModelPicker(ModelSelectionStore.Snapshot snapshot)
    {
        if (!ViaDriver || InputOverride != Console.In) return false;
        var active = App.ActiveProvider;
        var provider = active is null ? null : new ProviderConfig
        {
            Name = active.Name, Endpoint = active.Endpoint, ApiKeyEnvVar = active.ApiKeyEnvVar,
            Headers = active.Headers is null ? null : new Dictionary<string, string>(active.Headers),
        };
        var view = new Tui.ModelPickerView(snapshot.Slots, provider?.Name ?? "No active provider");
        string? saved = null;
        bool handled = _driver!.RunModelPicker(view,
            ct => ProviderModelCatalog.LoadAsync(provider, ct),
            (id, model, effort) =>
            {
                if (!ReferenceEquals(App.ActiveProvider, active))
                    throw new InvalidOperationException("Provider changed while the picker was open.");
                App.SwarmConfig = ModelSelectionStore.Save(snapshot, id, model, effort);
                saved = $"Saved {view.Slot!.Label}: {model} · effort {effort ?? "default (no override)"}. Applies on the next run.";
            });
        if (saved is not null) WriteSuccess(saved);
        return handled;
    }
}
