using MuxSwarm.Utils;

namespace MuxSwarm.Tests.Tests;

public class CronExpressionTests
{
    // ── Parse validation ──────────────────────────────────────────────

    [Fact]
    public void Parse_ValidEveryMinute_ReturnsNonNull()
    {
        var cron = CronExpression.Parse("* * * * *");
        Assert.NotNull(cron);
    }

    [Fact]
    public void Parse_ValidSpecificTime_ReturnsNonNull()
    {
        var cron = CronExpression.Parse("30 14 1 6 0");
        Assert.NotNull(cron);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("* * *")]
    [InlineData("* * * * * *")]
    [InlineData("abc")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 32 * *")]
    [InlineData("* * * 13 *")]
    [InlineData("* * * * 7")]
    public void Parse_InvalidExpressions_ReturnsNull(string expression)
    {
        Assert.Null(CronExpression.Parse(expression));
    }

    // ── GetNextOccurrence: every minute ────────────────────────────────

    [Fact]
    public void GetNextOccurrence_EveryMinute_ReturnsNextMinute()
    {
        var cron = CronExpression.Parse("* * * * *");
        Assert.NotNull(cron);

        var after = new DateTime(2026, 3, 31, 12, 0, 0);
        var next = cron.GetNextOccurrence(after);

        Assert.NotNull(next);
        Assert.Equal(after.AddMinutes(1), next.Value);
    }

    // ── GetNextOccurrence: specific minute ─────────────────────────────

    [Fact]
    public void GetNextOccurrence_SpecificMinute_ReturnsCorrectTime()
    {
        var cron = CronExpression.Parse("30 * * * *");
        Assert.NotNull(cron);

        var after = new DateTime(2026, 3, 31, 12, 0, 0);
        var next = cron.GetNextOccurrence(after);

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 3, 31, 12, 30, 0), next.Value);
    }

    [Fact]
    public void GetNextOccurrence_SpecificMinute_PastInCurrentHour_ReturnsNextHour()
    {
        var cron = CronExpression.Parse("15 * * * *");
        Assert.NotNull(cron);

        var after = new DateTime(2026, 3, 31, 12, 30, 0);
        var next = cron.GetNextOccurrence(after);

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 3, 31, 13, 15, 0), next.Value);
    }

    // ── GetNextOccurrence: specific hour and minute ────────────────────

    [Fact]
    public void GetNextOccurrence_SpecificHourAndMinute_ReturnsCorrectTime()
    {
        var cron = CronExpression.Parse("0 9 * * *");
        Assert.NotNull(cron);

        var after = new DateTime(2026, 3, 31, 8, 0, 0);
        var next = cron.GetNextOccurrence(after);

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 3, 31, 9, 0, 0), next.Value);
    }

    [Fact]
    public void GetNextOccurrence_SpecificHourAndMinute_AfterTarget_ReturnsNextDay()
    {
        var cron = CronExpression.Parse("0 9 * * *");
        Assert.NotNull(cron);

        var after = new DateTime(2026, 3, 31, 10, 0, 0);
        var next = cron.GetNextOccurrence(after);

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 4, 1, 9, 0, 0), next.Value);
    }

    // ── GetNextOccurrence: step values ─────────────────────────────────

    [Fact]
    public void GetNextOccurrence_EveryFiveMinutes_ReturnsCorrectTime()
    {
        var cron = CronExpression.Parse("*/5 * * * *");
        Assert.NotNull(cron);

        var after = new DateTime(2026, 3, 31, 12, 3, 0);
        var next = cron.GetNextOccurrence(after);

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 3, 31, 12, 5, 0), next.Value);
    }

    [Fact]
    public void GetNextOccurrence_EveryFiveMinutes_OnBoundary_ReturnsNextBoundary()
    {
        var cron = CronExpression.Parse("*/5 * * * *");
        Assert.NotNull(cron);

        var after = new DateTime(2026, 3, 31, 12, 5, 0);
        var next = cron.GetNextOccurrence(after);

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 3, 31, 12, 10, 0), next.Value);
    }

    // ── GetNextOccurrence: comma-separated values ──────────────────────

    [Fact]
    public void GetNextOccurrence_CommaSeparatedMinutes_ReturnsCorrectTime()
    {
        var cron = CronExpression.Parse("0,30 * * * *");
        Assert.NotNull(cron);

        var after = new DateTime(2026, 3, 31, 12, 5, 0);
        var next = cron.GetNextOccurrence(after);

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 3, 31, 12, 30, 0), next.Value);
    }

    // ── GetNextOccurrence: range values ────────────────────────────────

    [Fact]
    public void GetNextOccurrence_RangeHours_ReturnsCorrectTime()
    {
        var cron = CronExpression.Parse("0 9-17 * * *");
        Assert.NotNull(cron);

        // Before the range
        var after1 = new DateTime(2026, 3, 31, 8, 30, 0);
        var next1 = cron.GetNextOccurrence(after1);
        Assert.NotNull(next1);
        Assert.Equal(new DateTime(2026, 3, 31, 9, 0, 0), next1.Value);

        // After the range
        var after2 = new DateTime(2026, 3, 31, 18, 0, 0);
        var next2 = cron.GetNextOccurrence(after2);
        Assert.NotNull(next2);
        Assert.Equal(new DateTime(2026, 4, 1, 9, 0, 0), next2.Value);
    }

    // ── GetNextOccurrence: day of month constraint ─────────────────────

    [Fact]
    public void GetNextOccurrence_SpecificDayOfMonth_ReturnsCorrectDate()
    {
        var cron = CronExpression.Parse("0 0 15 * *");
        Assert.NotNull(cron);

        var after = new DateTime(2026, 3, 1, 0, 0, 0);
        var next = cron.GetNextOccurrence(after);

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 3, 15, 0, 0, 0), next.Value);
    }

    // ── GetNextOccurrence: month constraint ────────────────────────────

    [Fact]
    public void GetNextOccurrence_SpecificMonth_ReturnsCorrectDate()
    {
        var cron = CronExpression.Parse("0 0 1 6 *");
        Assert.NotNull(cron);

        var after = new DateTime(2026, 1, 1, 0, 0, 0);
        var next = cron.GetNextOccurrence(after);

        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 6, 1, 0, 0, 0), next.Value);
    }

    // ── GetNextOccurrence: day of week constraint ──────────────────────

    [Fact]
    public void GetNextOccurrence_DayOfWeek_Monday()
    {
        // 2026-03-31 is a Tuesday, so next Monday is 2026-04-06
        var cron = CronExpression.Parse("0 9 * * 1"); // Monday=1
        Assert.NotNull(cron);

        var after = new DateTime(2026, 3, 31, 10, 0, 0);
        var next = cron.GetNextOccurrence(after);

        Assert.NotNull(next);
        Assert.Equal(DayOfWeek.Monday, next.Value.DayOfWeek);
        Assert.Equal(9, next.Value.Hour);
    }

    // ── GetNextOccurrence: seconds are zeroed ──────────────────────────

    [Fact]
    public void GetNextOccurrence_AlwaysReturnsZeroSeconds()
    {
        var cron = CronExpression.Parse("* * * * *");
        Assert.NotNull(cron);

        var after = new DateTime(2026, 3, 31, 12, 0, 45);
        var next = cron.GetNextOccurrence(after);

        Assert.NotNull(next);
        Assert.Equal(0, next.Value.Second);
    }
}
