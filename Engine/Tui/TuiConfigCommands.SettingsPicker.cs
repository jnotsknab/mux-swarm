using System.Text.Json;
using System.Text.RegularExpressions;

namespace MuxSwarm.Engine.Tui;

internal static partial class TuiConfigCommands
{
    /// <summary>Detached UI data only; no setter closures or secret values are retained for browsing.</summary>
    internal sealed record SettingSnapshot(string Name, string[] Aliases, string Current, string ValueHint,
        string Description, bool Sensitive);
    /// <summary>Explicit Apply intent for one canonical setting and its staged value.</summary>
    internal sealed record SettingSelection(string Name, string Value);
    /// <summary>Detect stale picker baselines before invoking legacy setter side effects.</summary>
    internal sealed record PickerBaseline(AppConfig Config, SwarmConfig? Swarm, string ConfigJson, string SwarmJson,
        string ConfigPath, string SwarmPath, byte[]? ConfigBytes, byte[]? SwarmBytes);

    internal static bool IsSensitiveSetting(string name)
        => Regex.IsMatch(name, @"password|secret|token(?!s|threshold|budget)|api.?key|credential|connectionstring|privatekey|authorization|endpoint|(?:^|\.)\w*(?:url|uri|command|args|connectionstring)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Read the existing registry without materializing missing objects or exposing secret values.</summary>
    internal static IReadOnlyList<SettingSnapshot> GetSettingSnapshots()
        => AllKeys(materializeNulls: false).Select(k =>
        {
            bool sensitive = IsSensitiveSetting(k.Name);
            string current;
            try { current = sensitive ? "(masked)" : k.Get(); }
            catch { current = "(unavailable)"; }
            return new SettingSnapshot(k.Name, k.Aliases.ToArray(), current, k.ValueHint,
                DescribeSetting(k.Name), sensitive);
        }).ToArray();

    private static string DescribeSetting(string name) => name switch
    {
        "renderMode" => "Console rendering mode; takes effect on next launch.",
        "renderEngine" => "Inline scrollback or frame renderer; takes effect on next launch.",
        "toolOutput" => "Compact summaries or full tool output; applies live.",
        "collapseToolLines" => "Auto-collapse threshold in lines; zero disables it. Applies live.",
        "delegationSpacing" => "Blank lines after tool and delegation output; applies live.",
        "mouseTracking" => "Frame mouse tracking; inline retains native scrollback/selection.",
        "scrollSpeedRows" => "Rows per frame scroll shortcut; applies live.",
        "dockedFooter" => "Show the docked footer; takes effect on next launch.",
        "bracketedPaste" => "Terminal bracketed-paste framing; applies live.",
        "showReasoning" => "Reasoning text visibility; applies live.",
        "ultra.autoSubAgents" => "Automatically enable parallel delegation when ultra is enabled.",
        "ultra.includeSubAgents" => "Apply ultra reasoning guidance to delegated agents.",
        "ultra.thinkingBudget" => "Ultra model thinking budget for subsequent sessions.",
        "serveAddress" => "Serve listener address; applies on next --serve.",
        "serve.auth.enabled" => "Serve authentication requirement; applies on next --serve.",
        _ => (name.StartsWith("swarm.", StringComparison.OrdinalIgnoreCase) ? "Swarm setting; /refresh applies persisted changes. " : "Configuration setting; effect follows existing /set behavior. ")
            + Regex.Replace(name.Split('.').Last(), "([a-z])([A-Z])", "$1 $2") + "."
    };

    private static byte[]? ReadBaseline(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
    private static bool SameBytes(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

    /// <summary>Snapshot identities and on-disk bytes; no config files are created or reloaded.</summary>
    internal static PickerBaseline CapturePickerBaseline()
        => new(Cfg, App.SwarmConfig, JsonSerializer.Serialize(Cfg), JsonSerializer.Serialize(App.SwarmConfig),
            PlatformContext.ConfigPath, PlatformContext.SwarmPath,
            ReadBaseline(PlatformContext.ConfigPath), ReadBaseline(PlatformContext.SwarmPath));

    /// <summary>Re-resolve the current registry and invoke its original validator/save/live setter once.
    /// This is not a new persistence transaction; legacy post-Apply failure behavior is retained.</summary>
    internal static Result ApplyPickerSelection(SettingSelection selection, PickerBaseline baseline)
    {
        if (string.IsNullOrWhiteSpace(selection.Value)) return Bad("Enter a value, or Esc to cancel. Use null explicitly where supported.");
        try
        {
            if (!ReferenceEquals(Cfg, baseline.Config) || !ReferenceEquals(App.SwarmConfig, baseline.Swarm)
                || JsonSerializer.Serialize(Cfg) != baseline.ConfigJson || JsonSerializer.Serialize(App.SwarmConfig) != baseline.SwarmJson
                || PlatformContext.ConfigPath != baseline.ConfigPath || PlatformContext.SwarmPath != baseline.SwarmPath
                || !SameBytes(ReadBaseline(baseline.ConfigPath), baseline.ConfigBytes) || !SameBytes(ReadBaseline(baseline.SwarmPath), baseline.SwarmBytes))
                return Bad("Configuration changed while the picker was open. Close and reopen /set before applying.");
            var key = Find(selection.Name);
            if (key is null)
            {
                var detached = AllKeys(materializeNulls: false).FirstOrDefault(k => k.Matches(selection.Name));
                if (detached is null) return Bad("Selected setting is no longer available. Close and reopen /set.");
                if (detached.Validate is { } validate && !validate(selection.Value.Trim()).ok)
                    return Bad($"Value rejected for {detached.Name}. Expected {detached.ValueHint}; edit the draft and retry.");
                // Only a valid explicit Apply may materialize live default branches.
                key = FindAny(selection.Name);
            }
            if (key is null) return Bad("Selected setting is no longer available. Close and reopen /set.");
            var result = key.Set(selection.Value.Trim());
            if (!result.Ok) return Bad($"Could not apply/save {key.Name}. Expected {key.ValueHint}; check the value and configuration before retrying.");
            if (IsSensitiveSetting(key.Name))
                return Ok($"{key.Name} updated (value masked). Saved{(key.Name.StartsWith("swarm.", StringComparison.OrdinalIgnoreCase) ? "; run /refresh to apply" : "") }.");
            return result;
        }
        catch
        {
            return Bad("Could not apply/save this setting. Existing live effects may already have run; inspect configuration before retrying. Values omitted.");
        }
    }
}
