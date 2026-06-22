using Microsoft.Extensions.AI;

namespace MuxSwarm.Utils;

/// <summary>
/// Provider-aware reasoning escalation for /ultra mode. Applied to a freshly built
/// <see cref="ChatOptions"/> only while ultra is active; because escalation is gated on
/// the App.UltraMode flag at build time and ChatOptions are reconstructed per run, toggling
/// ultra off naturally restores prior reasoning with no persistent mutation of config.
/// </summary>
public static class UltraReasoning
{
    /// <summary>
    /// Escalates reasoning on <paramref name="opts"/> to the maximum the active provider accepts:
    /// a provider-native numeric thinking budget (sidesteps the effort-enum xhigh/E2 path) AND an
    /// effort tier. The effort tier is set to <see cref="ReasoningEffort.High"/> rather than
    /// ExtraHigh until E2 lands, since extra_high serializes to xhigh and is rejected on the
    /// CLIProxy/Claude path; the numeric budget carries the real escalation on budget-capable
    /// providers.
    /// </summary>
    public static void Apply(ChatOptions opts)
    {
        var budget = App.Config.Ultra.ThinkingBudget;

        // Numeric budget path — forwarded as an arbitrary provider param, exactly like
        // ModelOpts.AdditionalParams. Providers that honor a numeric thinking budget
        // (e.g. Anthropic thinking.budget_tokens) pick it up; others ignore it.
        if (budget > 0)
        {
            opts.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            opts.AdditionalProperties["thinking"] = new Dictionary<string, object>
            {
                ["type"] = "enabled",
                ["budget_tokens"] = budget
            };
        }

        // Effort tier — set to High (capped below ExtraHigh until E2 lands so the
        // extra_high -> xhigh serialization is not rejected on the CLIProxy/Claude path).
        opts.Reasoning = new ReasoningOptions
        {
            Effort = ReasoningEffort.High,
            Output = opts.Reasoning?.Output
        };
    }
}
