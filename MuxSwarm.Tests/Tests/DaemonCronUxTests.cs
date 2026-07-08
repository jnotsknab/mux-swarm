using MuxSwarm.Utils;
using MuxSwarm.State;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Covers the user-friendly /daemon cron|watch surface (g12.x): natural-language cron translation
/// (deterministic paths - no model call), CronExpression.Describe, and DaemonCommand's mode-token
/// parsing (agent|swarm|pswarm|agent:Name).
/// </summary>
public class DaemonCronUxTests
{
    // ── Natural-language cron: deterministic patterns produce valid cron ──

    [Theory]
    [InlineData("every minute", "* * * * *")]
    [InlineData("hourly", "0 * * * *")]
    [InlineData("every hour", "0 * * * *")]
    [InlineData("daily", "0 0 * * *")]
    [InlineData("nightly", "0 0 * * *")]
    [InlineData("weekly", "0 0 * * 0")]
    [InlineData("monthly", "0 0 1 * *")]
    [InlineData("every 5 minutes", "*/5 * * * *")]
    [InlineData("every 2 hours", "0 */2 * * *")]
    [InlineData("every day at 9am", "0 9 * * *")]
    [InlineData("daily at 09:30", "30 9 * * *")]
    [InlineData("every day at 5pm", "0 17 * * *")]
    [InlineData("weekdays at 9am", "0 9 * * 1-5")]
    [InlineData("weekends at noon", "0 12 * * 0,6")]
    [InlineData("every monday at 8am", "0 8 * * 1")]
    [InlineData("at midnight", "0 0 * * *")]
    [InlineData("at 14:15", "15 14 * * *")]
    public void ResolveDeterministic_Pattern_ProducesExpectedCron(string phrase, string expected)
    {
        var (cron, src) = CronNaturalLanguage.ResolveDeterministic(phrase);
        Assert.Equal(expected, cron);
        Assert.Equal(CronNaturalLanguage.Source.Pattern, src);
        Assert.NotNull(CronExpression.Parse(cron!));
    }

    [Fact]
    public void ResolveDeterministic_RawCron_UsedVerbatim()
    {
        var (cron, src) = CronNaturalLanguage.ResolveDeterministic("*/15 9-17 * * 1-5");
        Assert.Equal("*/15 9-17 * * 1-5", cron);
        Assert.Equal(CronNaturalLanguage.Source.Raw, src);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sometime next tuesday-ish")]
    [InlineData("every fortnight")]
    public void ResolveDeterministic_Unrecognized_ReturnsNone(string phrase)
    {
        var (cron, src) = CronNaturalLanguage.ResolveDeterministic(phrase);
        Assert.Null(cron);
        Assert.Equal(CronNaturalLanguage.Source.None, src);
    }

    // ── Describe ──

    [Theory]
    [InlineData("*/5 * * * *", "every 5 minute(s)")]
    [InlineData("* * * * *", "every minute")]
    [InlineData("0 * * * *", "hourly at :00")]
    [InlineData("30 9 * * *", "daily at 09:30")]
    [InlineData("0 9 * * 1-5", "at 09:00 on weekdays")]
    [InlineData("0 12 * * 0,6", "at 12:00 on weekends")]
    [InlineData("0 8 * * 1", "at 08:00 on Mon")]
    public void Describe_CommonShapes_ReadsCleanly(string cron, string expected)
    {
        Assert.Equal(expected, CronExpression.Describe(cron));
    }

    [Fact]
    public void Describe_Garbage_EchoesRaw()
    {
        Assert.Equal("not a cron", CronExpression.Describe("not a cron"));
        Assert.Equal("(no schedule)", CronExpression.Describe(""));
    }

    // ── Mode-token parsing (via reflection - the helper is private) ──

    private static (string Mode, string? Agent) ParseModeToken(string token)
    {
        var mi = typeof(DaemonCommand).GetMethod("ParseModeToken",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var res = mi.Invoke(null, new object[] { token })!;
        var t = (System.Runtime.CompilerServices.ITuple)res;
        return ((string)t[0]!, (string?)t[1]);
    }

    [Theory]
    [InlineData("agent", "agent", null)]
    [InlineData("swarm", "swarm", null)]
    [InlineData("pswarm", "pswarm", null)]
    [InlineData("AGENT", "agent", null)]
    [InlineData("nonsense", "agent", null)]
    public void ParseModeToken_BareModes(string token, string mode, string? agent)
    {
        var (m, a) = ParseModeToken(token);
        Assert.Equal(mode, m);
        Assert.Equal(agent, a);
    }

    [Fact]
    public void ParseModeToken_AgentColonName_KeepsUnmatchedNameVerbatim()
    {
        // With no swarm.json roster in the test host, an unknown name is passed through as typed.
        var (m, a) = ParseModeToken("agent:CodeAgent");
        Assert.Equal("agent", m);
        Assert.Equal("CodeAgent", a);
    }
}
