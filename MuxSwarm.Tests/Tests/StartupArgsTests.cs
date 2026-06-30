using MuxSwarm;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Coverage for startup-args merging (config.startupArgs -> argv) and the shell-like tokenizer
/// that backs it. These let a user boot straight into a mode/agent every launch.
/// </summary>
public class StartupArgsTests
{
    [Fact]
    public void Tokenize_SplitsOnWhitespace()
    {
        var t = App.TokenizeArgString("--agent CodeAgent --giga");
        Assert.Equal(new[] { "--agent", "CodeAgent", "--giga" }, t);
    }

    [Fact]
    public void Tokenize_RespectsDoubleQuotes()
    {
        var t = App.TokenizeArgString("--goal \"do the thing\" --plan");
        Assert.Equal(new[] { "--goal", "do the thing", "--plan" }, t);
    }

    [Fact]
    public void Tokenize_EmptyOrWhitespaceYieldsNothing()
    {
        Assert.Empty(App.TokenizeArgString(""));
        Assert.Empty(App.TokenizeArgString("   "));
    }

    [Fact]
    public void Tokenize_CollapsesRepeatedSpaces()
    {
        var t = App.TokenizeArgString("--swarm    --ultra");
        Assert.Equal(new[] { "--swarm", "--ultra" }, t);
    }

    [Fact]
    public void Merge_PrependsStartupArgsBeforeArgv()
    {
        var merged = App.MergeStartupArgs("--agent CodeAgent", new[] { "--giga" });
        Assert.Equal(new[] { "--agent", "CodeAgent", "--giga" }, merged);
    }

    [Fact]
    public void Merge_RealArgvComesLastSoItCanOverride()
    {
        // Startup args first, real CLI args after -> a later single-valued flag wins.
        var merged = App.MergeStartupArgs("--model a", new[] { "--model", "b" });
        Assert.Equal(new[] { "--model", "a", "--model", "b" }, merged);
        Assert.Equal("b", merged[^1]);
    }

    [Fact]
    public void Merge_NoStartupArgsReturnsArgvUnchanged()
    {
        var argv = new[] { "--serve", "6723" };
        Assert.Same(argv, App.MergeStartupArgs("", argv));
        Assert.Same(argv, App.MergeStartupArgs(null, argv));
        Assert.Same(argv, App.MergeStartupArgs("   ", argv));
    }
}
