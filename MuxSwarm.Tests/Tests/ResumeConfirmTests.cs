using MuxSwarm.Engine;
using Xunit;

namespace MuxSwarm.Tests.Tests;

// /resume inside a live session: switches without the "end this session?" question unless
// console.confirmResume is on. The confirm reads stdin in stdio mode, so "n" = declined.
[Collection("ConsoleState")]
public sealed class ResumeConfirmTests : IDisposable
{
    private readonly AppConfig _config = App.Config;
    private readonly TextWriter _out = Console.Out;

    public ResumeConfirmTests()
    {
        Console.SetOut(TextWriter.Null);
        MuxConsole.StdioMode = true;
    }

    public void Dispose()
    {
        App.Config = _config;
        Console.SetOut(_out);
        MuxConsole.InputOverride = Console.In;
        MuxConsole.StdioMode = false;
        SingleAgentOrchestrator.PendingReplCommand = null;
    }

    private static bool Ask(string cmd, bool confirmResume, string answer)
    {
        App.Config = new AppConfig { Console = new ConsoleConfig { ConfirmResume = confirmResume } };
        MuxConsole.InputOverride = new StringReader(answer + "\n");
        return MetaCommandDispatch.ConfirmEndSession(cmd);
    }

    [Fact]
    public void Resume_SwitchesWithoutAsking_ByDefault()
    {
        Assert.False(new ConsoleConfig().ConfirmResume);
        Assert.True(Ask("/resume", confirmResume: false, answer: "n"));   // "n" never read: no question
        Assert.True(Ask("/RESUME", confirmResume: false, answer: "n"));
    }

    [Fact]
    public void Resume_Asks_WhenConfirmResumeIsOn()
    {
        Assert.False(Ask("/resume", confirmResume: true, answer: "n"));
        Assert.True(Ask("/resume", confirmResume: true, answer: "y"));
    }

    [Fact]
    public void OtherMenuCommands_StillAsk()
    {
        Assert.False(Ask("/setmodel", confirmResume: false, answer: "n"));
        Assert.True(Ask("/setmodel", confirmResume: false, answer: "y"));
    }

    [Fact]
    public async Task Resume_InASession_QuitsToTheMenu_WithTheFullLine()
    {
        App.Config = new AppConfig();
        MuxConsole.InputOverride = new StringReader("n\n");
        var r = await MetaCommandDispatch.TryHandleAsync("/resume 2026-10-20_17-52-09", null, null, CancellationToken.None);
        Assert.Equal(MetaCommandDispatch.Result.QuitToMenu, r);
        Assert.Equal("/resume 2026-10-20_17-52-09", SingleAgentOrchestrator.PendingReplCommand);
    }

    [Fact]
    public void ConfirmResume_RoundTripsInConfigJson()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new AppConfig { Console = new ConsoleConfig { ConfirmResume = true } });
        Assert.Contains("\"confirmResume\":true", json);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppConfig>("{\"console\":{\"confirmResume\":true}}")!;
        Assert.True(back.Console.ConfirmResume);
    }
}
