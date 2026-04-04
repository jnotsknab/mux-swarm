namespace MuxSwarm.Utils;

/// <summary>
/// Minimal 5-field cron expression parser with no external dependencies.
/// Supports: *, */N, N, N-M, N,M,O
/// Fields: minute hour day-of-month month day-of-week
/// </summary>
internal sealed class CronExpression
{
    private readonly HashSet<int> _minutes;
    private readonly HashSet<int> _hours;
    private readonly HashSet<int> _daysOfMonth;
    private readonly HashSet<int> _months;
    private readonly HashSet<int> _daysOfWeek;

    private CronExpression(
        HashSet<int> minutes, HashSet<int> hours,
        HashSet<int> daysOfMonth, HashSet<int> months,
        HashSet<int> daysOfWeek)
    {
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
    }

    public static CronExpression? Parse(string expression)
    {
        var parts = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return null;

        try
        {
            return new CronExpression(
                minutes: ParseField(parts[0], 0, 59),
                hours: ParseField(parts[1], 0, 23),
                daysOfMonth: ParseField(parts[2], 1, 31),
                months: ParseField(parts[3], 1, 12),
                daysOfWeek: ParseField(parts[4], 0, 6)
            );
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the next occurrence after the given time, or null if none
    /// found within 366 days.
    /// </summary>
    public DateTime? GetNextOccurrence(DateTime after)
    {
        var candidate = new DateTime(after.Year, after.Month, after.Day, after.Hour, after.Minute, 0)
            .AddMinutes(1);

        var limit = after.AddDays(366);

        while (candidate < limit)
        {
            if (!_months.Contains(candidate.Month))
            {
                candidate = new DateTime(candidate.Year, candidate.Month, 1).AddMonths(1);
                continue;
            }

            if (!_daysOfMonth.Contains(candidate.Day) ||
                !_daysOfWeek.Contains((int)candidate.DayOfWeek))
            {
                candidate = candidate.Date.AddDays(1);
                continue;
            }

            if (!_hours.Contains(candidate.Hour))
            {
                candidate = new DateTime(candidate.Year, candidate.Month, candidate.Day,
                    candidate.Hour, 0, 0).AddHours(1);
                continue;
            }

            if (!_minutes.Contains(candidate.Minute))
            {
                candidate = candidate.AddMinutes(1);
                continue;
            }

            return candidate;
        }

        return null;
    }

    private static HashSet<int> ParseField(string field, int min, int max)
    {
        var values = new HashSet<int>();

        foreach (var part in field.Split(','))
        {
            var trimmed = part.Trim();

            // */N -- step from min
            if (trimmed.StartsWith("*/"))
            {
                var step = int.Parse(trimmed[2..]);
                for (int i = min; i <= max; i += step)
                    values.Add(i);
                continue;
            }

            // * -- all values
            if (trimmed == "*")
            {
                for (int i = min; i <= max; i++)
                    values.Add(i);
                continue;
            }

            // N-M -- range
            if (trimmed.Contains('-'))
            {
                var rangeParts = trimmed.Split('-', 2);
                var start = int.Parse(rangeParts[0]);
                var end = int.Parse(rangeParts[1]);
                if (start < min || end > max)
                    throw new ArgumentOutOfRangeException(nameof(field),
                        $"Range {start}-{end} is out of bounds [{min}-{max}]");
                for (int i = start; i <= end; i++)
                    values.Add(i);
                continue;
            }

            // N -- single value
            var val = int.Parse(trimmed);
            if (val < min || val > max)
                throw new ArgumentOutOfRangeException(nameof(field),
                    $"Value {val} is out of bounds [{min}-{max}]");
            values.Add(val);
        }

        return values;
    }
}