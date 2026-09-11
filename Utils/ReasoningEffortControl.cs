using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace MuxSwarm.Utils;

/// <summary>Session effort selection and explicit OpenAI-compatible wire values beyond the MEAI enum.</summary>
internal static class ReasoningEffortControl
{
    private const string RawEffortKey = "mux.internal.reasoning-effort";
    private static readonly string[] CycleTiers = ["low", "med", "high", "xhigh", "max"];

    internal const string Usage = "Use /effort [low|med|high|xhigh|max], /effort custom <raw-value>, or /max.";

    /// <summary>A canonical UI label and either a portable enum tier or an explicit wire value.</summary>
    internal sealed record Selection(string Label, ReasoningEffort? Effort, string? RawValue = null);

    /// <summary>Parses preset aliases or a custom value, preserving custom casing and interior spaces.</summary>
    internal static bool TryParse(string? argument, out Selection? selection)
    {
        selection = null;
        if (argument is null || argument.Any(char.IsControl)) return false;
        var value = argument.Trim();
        if (value.Length == 0) return false;

        if (value.StartsWith("custom ", StringComparison.OrdinalIgnoreCase))
        {
            var raw = value[7..].Trim();
            if (raw.Length == 0) return false;
            selection = new Selection("custom " + raw, null, raw);
            return true;
        }

        selection = value.ToLowerInvariant() switch
        {
            "none" => new Selection("none", ReasoningEffort.None),
            "low" or "l" => new Selection("low", ReasoningEffort.Low),
            "medium" or "med" or "m" => new Selection("med", ReasoningEffort.Medium),
            "high" or "h" => new Selection("high", ReasoningEffort.High),
            "xhigh" or "extrahigh" or "extra_high" or "xh" => new Selection("xhigh", ReasoningEffort.ExtraHigh),
            "max" => new Selection("max", null, "max"),
            _ => null
        };
        return selection is not null;
    }

    /// <summary>Updates only the selected effort, retaining reasoning output and unrelated model options.</summary>
    internal static void Apply(ChatOptions options, Selection selection)
    {
        options.Reasoning = new ReasoningOptions
        {
            Effort = selection.Effort,
            Output = options.Reasoning?.Output
        };
        if (selection.RawValue is not null)
        {
            options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            options.AdditionalProperties[RawEffortKey] = selection;
        }
        else
        {
            options.AdditionalProperties?.Remove(RawEffortKey);
        }
    }

    /// <summary>Returns the selected UI label; raw custom values remain visibly distinct from presets.</summary>
    internal static string? GetLabel(ChatOptions options)
    {
        if (GetExplicit(options) is { } explicitEffort) return explicitEffort.Label;
        return options.Reasoning?.Effort switch
        {
            ReasoningEffort.None => "none",
            ReasoningEffort.Low => "low",
            ReasoningEffort.Medium => "med",
            ReasoningEffort.High => "high",
            ReasoningEffort.ExtraHigh => "xhigh",
            _ => null
        };
    }

    /// <summary>Cycles low through max, wrapping from max, custom, none, or an unset effort to low.</summary>
    internal static string Cycle(ChatOptions options)
    {
        var index = Array.IndexOf(CycleTiers, GetLabel(options));
        var label = CycleTiers[(index + 1) % CycleTiers.Length];
        TryParse(label, out var selection);
        Apply(options, selection!);
        return label;
    }

    /// <summary>Handles only /effort and /max; an invalid argument never changes the selection.</summary>
    internal static bool TryHandleCommand(ChatOptions options, string command, out string? label, out string? error)
    {
        label = error = null;
        var parts = command.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        var isMax = parts[0].Equals("/max", StringComparison.OrdinalIgnoreCase);
        if (!isMax && !parts[0].Equals("/effort", StringComparison.OrdinalIgnoreCase)) return false;
        if (isMax && parts.Length != 1)
        {
            error = Usage;
            return true;
        }
        if (!isMax && parts.Length == 1)
        {
            label = Cycle(options);
            return true;
        }
        if (!TryParse(isMax ? "max" : parts[1], out var selection))
        {
            error = Usage;
            return true;
        }
        Apply(options, selection!);
        label = selection!.Label;
        return true;
    }

    /// <summary>Returns the immutable raw selection carried through ChatOptions.Clone and option merges.</summary>
    internal static Selection? GetExplicit(ChatOptions? options)
        => options?.AdditionalProperties?.TryGetValue(RawEffortKey, out var value) == true
            ? value as Selection : null;

    /// <summary>Creates per-request native options for max/custom without mutating the caller or enabling fallback.</summary>
    internal static ChatOptions PrepareExplicit(ChatOptions options, Selection selection)
    {
        var prepared = options.Clone();
        prepared.AdditionalProperties?.Remove(RawEffortKey);
        var previousFactory = options.RawRepresentationFactory;
        var rawValue = selection.RawValue!;
        prepared.RawRepresentationFactory = client =>
        {
            var representation = previousFactory?.Invoke(client);
            if (representation is not null && representation is not ChatCompletionOptions)
                throw new InvalidOperationException("Explicit reasoning effort requires OpenAI ChatCompletionOptions.");
            // The MEAI factory contract requires a fresh native options object for every invocation.
            var native = representation as ChatCompletionOptions ?? new ChatCompletionOptions();
#pragma warning disable OPENAI001 // Extensible string tier supports provider values absent from the MEAI enum.
            native.ReasoningEffortLevel = new ChatReasoningEffortLevel(rawValue);
#pragma warning restore OPENAI001
            return native;
        };
        return prepared;
    }
}
