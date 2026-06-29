namespace MuxSwarm.Utils;

/// <summary>
/// Minimal, optional model-pricing table for the /cost gauge. USD per 1M tokens, matched by a
/// case-insensitive SUBSTRING on the active model id (so "claude-opus-4-6" hits the "claude-opus"
/// entry). Unknown models return null and /cost renders "unknown" rather than guessing.
///
/// These are coarse list-price approximations for direct-API usage and are NOT authoritative -
/// users on flat-rate SUBSCRIPTION providers (the CLIProxy sidecar / Max plan) pay no per-token
/// cost, and /cost shows usage-only for those. Override or extend via <see cref="Overrides"/>.
/// </summary>
public static class ModelPricing
{
    public readonly record struct Price(double InputPer1M, double OutputPer1M);

    /// <summary>User/runtime overrides, consulted before the built-in table (longest-key-first).</summary>
    public static readonly Dictionary<string, Price> Overrides =
        new(StringComparer.OrdinalIgnoreCase);

    // Built-in coarse list prices (USD / 1M tokens). Substring keys, longest match wins.
    private static readonly (string Key, Price Price)[] Table =
    {
        ("claude-opus",     new Price(15.00, 75.00)),
        ("claude-sonnet",   new Price(3.00, 15.00)),
        ("claude-haiku",    new Price(0.80, 4.00)),
        ("opus",            new Price(15.00, 75.00)),
        ("sonnet",          new Price(3.00, 15.00)),
        ("haiku",           new Price(0.80, 4.00)),
        ("gpt-5-codex",     new Price(1.25, 10.00)),
        ("gpt-5-mini",      new Price(0.25, 2.00)),
        ("gpt-5",           new Price(1.25, 10.00)),
        ("codex",           new Price(1.25, 10.00)),
        ("o3",              new Price(2.00, 8.00)),
        ("gpt-4o-mini",     new Price(0.15, 0.60)),
        ("gpt-4o",          new Price(2.50, 10.00)),
        ("gemini-2.5-pro",  new Price(1.25, 10.00)),
        ("gemini-1.5-pro",  new Price(1.25, 5.00)),
        ("gemini-pro",      new Price(1.25, 10.00)),
        ("gemini-flash",    new Price(0.075, 0.30)),
        ("gemini",          new Price(1.25, 10.00)),
    };

    /// <summary>
    /// Look up a price for a model id by longest case-insensitive substring match. Overrides win;
    /// returns null when nothing matches (render "unknown").
    /// </summary>
    public static Price? Lookup(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;

        Price? best = null;
        int bestLen = -1;
        foreach (var (key, price) in Overrides)
        {
            if (modelId.Contains(key, StringComparison.OrdinalIgnoreCase) && key.Length > bestLen)
            {
                best = price;
                bestLen = key.Length;
            }
        }
        if (best is not null) return best;

        foreach (var (key, price) in Table)
        {
            if (modelId.Contains(key, StringComparison.OrdinalIgnoreCase) && key.Length > bestLen)
            {
                best = price;
                bestLen = key.Length;
            }
        }
        return best;
    }

    /// <summary>Estimate USD cost for a token split. Returns null when the model price is unknown.</summary>
    public static double? Estimate(string? modelId, long inputTokens, long outputTokens)
    {
        if (Lookup(modelId) is not { } p) return null;
        return inputTokens / 1_000_000.0 * p.InputPer1M
             + outputTokens / 1_000_000.0 * p.OutputPer1M;
    }
}
