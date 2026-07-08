using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Utils;

/// <summary>
/// Translates a user-friendly schedule phrase (e.g. "every weekday at 9am", "every 5 minutes")
/// into a standard 5-field cron expression. Resolution is layered so the common cases never spend
/// a model call:
///   1. RAW  - the input already parses as a 5-field cron (<see cref="CronExpression"/>): used verbatim.
///   2. PATTERN - a small set of deterministic natural-language regexes.
///   3. LLM  - anything else is handed to a light model, then re-validated with CronExpression.Parse.
/// Every path is validated before return; an unparseable/uncertain result yields null so the caller
/// can fall back to asking for a raw expression.
/// </summary>
public static class CronNaturalLanguage
{
    public enum Source { Raw, Pattern, Llm, None }

    /// <summary>Deterministic resolution only (no model call). Returns a validated cron + its source.</summary>
    public static (string? Cron, Source Source) ResolveDeterministic(string input)
    {
        var s = (input ?? string.Empty).Trim();
        if (s.Length == 0) return (null, Source.None);

        // 1. Already a valid raw cron -> verbatim.
        if (CronExpression.Parse(s) is not null) return (s, Source.Raw);

        // 2. Natural-language patterns.
        var cron = FromPattern(s);
        if (cron is not null && CronExpression.Parse(cron) is not null) return (cron, Source.Pattern);

        return (null, Source.None);
    }

    /// <summary>
    /// Full resolution: deterministic first, else a single light-model translation call (validated).
    /// Returns null when nothing produced a valid 5-field cron.
    /// </summary>
    public static async Task<(string? Cron, Source Source)> ResolveAsync(
        string input, IChatClient? client, ChatOptions? options, CancellationToken ct)
    {
        var (det, src) = ResolveDeterministic(input);
        if (det is not null) return (det, src);
        if (client is null) return (null, Source.None);

        var sys = new ChatMessage(ChatRole.System,
            "You convert a natural-language schedule into a standard 5-field cron expression " +
            "(minute hour day-of-month month day-of-week). Output ONLY the cron expression on a " +
            "single line - no prose, no quotes, no markdown. Fields use standard syntax (*, */N, " +
            "N, N-M, N,M). Day-of-week is 0-6 with 0=Sunday. If the request is ambiguous, choose " +
            "the most common interpretation.");
        var usr = new ChatMessage(ChatRole.User, "Schedule: " + input);

        string raw;
        try
        {
            var safe = options;
            if (safe?.Tools is { Count: > 0 })
            {
                safe = safe.Clone();
                safe.Tools = null;
                safe.ToolMode = ChatToolMode.None;
            }
            var resp = await client.GetResponseAsync(new[] { sys, usr }, safe, ct);
            raw = resp.Text ?? string.Empty;
        }
        catch (OperationCanceledException) { return (null, Source.None); }
        catch { return (null, Source.None); }

        var extracted = ExtractCron(raw);
        if (extracted is not null && CronExpression.Parse(extracted) is not null)
            return (extracted, Source.Llm);
        return (null, Source.None);
    }

    // Pull the first 5-field cron-looking token sequence out of a model response.
    private static string? ExtractCron(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        foreach (var line in raw.Split('\n'))
        {
            var t = line.Trim().Trim('`').Trim();
            if (t.Length == 0) continue;
            var toks = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (toks.Length >= 5)
            {
                var candidate = string.Join(' ', toks.Take(5));
                if (CronExpression.Parse(candidate) is not null) return candidate;
            }
        }
        return null;
    }

    // --- Deterministic natural-language patterns -------------------------------------------------

    private static string? FromPattern(string input)
    {
        var s = input.Trim().ToLowerInvariant().TrimEnd('.');

        if (s is "every minute" or "each minute" or "minutely") return "* * * * *";
        if (s is "hourly" or "every hour" or "each hour") return "0 * * * *";
        if (s is "daily" or "every day" or "each day" or "nightly") return "0 0 * * *";
        if (s is "weekly" or "every week") return "0 0 * * 0";
        if (s is "monthly" or "every month") return "0 0 1 * *";

        // "every N minutes"
        var m = Regex.Match(s, @"^every\s+(\d{1,2})\s*(?:min|mins|minute|minutes)$");
        if (m.Success) { int n = int.Parse(m.Groups[1].Value); if (n is >= 1 and <= 59) return $"*/{n} * * * *"; }

        // "every N hours"
        m = Regex.Match(s, @"^every\s+(\d{1,2})\s*(?:hr|hrs|hour|hours)$");
        if (m.Success) { int n = int.Parse(m.Groups[1].Value); if (n is >= 1 and <= 23) return $"0 */{n} * * *"; }

        // "(every day|daily|weekdays|weekends|every <dow>) at <time>"
        m = Regex.Match(s,
            @"^(?<scope>every day|daily|each day|weekdays?|every weekday|weekends?|every weekend|" +
            @"(?:every\s+)?(?:mondays?|tuesdays?|wednesdays?|thursdays?|fridays?|saturdays?|sundays?))\s+at\s+(?<time>.+)$");
        if (m.Success)
        {
            var (hh, mm) = ParseTime(m.Groups["time"].Value);
            if (hh >= 0)
            {
                string dow = ScopeToDow(m.Groups["scope"].Value);
                return $"{mm} {hh} * * {dow}";
            }
        }

        // bare "at <time>" -> daily at that time
        m = Regex.Match(s, @"^at\s+(?<time>.+)$");
        if (m.Success)
        {
            var (hh, mm) = ParseTime(m.Groups["time"].Value);
            if (hh >= 0) return $"{mm} {hh} * * *";
        }

        return null;
    }

    // Map a day-of-week scope word to a cron day-of-week field.
    private static string ScopeToDow(string scope)
    {
        scope = scope.Trim();
        if (scope.StartsWith("every ")) scope = scope.Substring(6).Trim();
        if (scope.StartsWith("weekday") || scope == "every weekday") return "1-5";
        if (scope.StartsWith("weekend")) return "0,6";
        var map = new Dictionary<string, string>
        {
            ["sunday"] = "0", ["monday"] = "1", ["tuesday"] = "2", ["wednesday"] = "3",
            ["thursday"] = "4", ["friday"] = "5", ["saturday"] = "6",
        };
        foreach (var kv in map)
            if (scope.StartsWith(kv.Key)) return kv.Value;
        return "*";
    }

    // Parse "9am", "9:30am", "17:00", "5 pm", "noon", "midnight" -> (hour 0-23, minute 0-59); (-1,-1) on failure.
    private static (int Hour, int Minute) ParseTime(string t)
    {
        t = t.Trim().ToLowerInvariant();
        if (t is "noon" or "midday") return (12, 0);
        if (t is "midnight") return (0, 0);

        var m = Regex.Match(t, @"^(\d{1,2})(?::(\d{2}))?\s*(am|pm)?$");
        if (!m.Success) return (-1, -1);

        int hour = int.Parse(m.Groups[1].Value);
        int minute = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        string ampm = m.Groups[3].Value;

        if (ampm == "am") { if (hour == 12) hour = 0; }
        else if (ampm == "pm") { if (hour != 12) hour += 12; }

        if (hour is < 0 or > 23 || minute is < 0 or > 59) return (-1, -1);
        return (hour, minute);
    }
}
