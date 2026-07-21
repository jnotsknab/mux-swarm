using MuxSwarm.Utils;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Covers the first-turn slash-command leak fix (SingleAgentOrchestrator.ClassifyFirstTurnInput).
/// Before the fix, any session-native command typed as the FIRST input of a fresh session fell
/// through MetaCommandDispatch (which only knows the session-agnostic set) and was sent to the
/// model as the opening goal. The classifier guarantees every KNOWN session-native command is
/// either dispatched directly or guarded (notice + re-prompt); only plain text and UNKNOWN slash
/// input classify as a goal. Pure-function tests; no file IO.
/// </summary>
public class FirstTurnCommandGuardTests
{
    // The internal enum cannot appear in a public test signature (CS0051), so expected
    // kinds are passed by name and parsed inside the body.
    [Theory]
    [InlineData("/doctor", "Doctor")]
    [InlineData("/diff", "Diff")]
    [InlineData("/cost", "Cost")]
    [InlineData("/cost all", "Cost")]
    [InlineData("/init", "Init")]
    [InlineData("/review", "Review")]
    public void StatelessCommands_DispatchDirectly(string input, string expectedKind)
        => Assert.Equal(
            System.Enum.Parse<SingleAgentOrchestrator.FirstTurnInputKind>(expectedKind),
            SingleAgentOrchestrator.ClassifyFirstTurnInput(input));

    [Theory]
    [InlineData("/")]
    [InlineData("/?")]
    public void Palette_IsRecognized(string input)
        => Assert.Equal(SingleAgentOrchestrator.FirstTurnInputKind.Palette,
            SingleAgentOrchestrator.ClassifyFirstTurnInput(input));

    [Theory]
    [InlineData("!git status")]
    [InlineData("!dir")]
    public void ShellBang_IsRecognized(string input)
        => Assert.Equal(SingleAgentOrchestrator.FirstTurnInputKind.Shell,
            SingleAgentOrchestrator.ClassifyFirstTurnInput(input));

    // The meaningless-on-turn-one session commands must be GUARDED, never a goal.
    [Theory]
    [InlineData("/compact")]
    [InlineData("/compact keep the build notes")]
    [InlineData("/handoff")]
    [InlineData("/heal")]
    [InlineData("/reflect deep")]
    [InlineData("/fix")]
    [InlineData("/wipe")]
    [InlineData("/tokens")]
    [InlineData("/context")]
    [InlineData("/undo")]
    [InlineData("/retry")]
    [InlineData("/redo")]
    [InlineData("/effort")]
    [InlineData("/effort high")]
    [InlineData("/tag my-session")]
    [InlineData("/update")]
    [InlineData("/detach")]
    public void SessionNativeCommands_AreGuarded(string input)
        => Assert.Equal(SingleAgentOrchestrator.FirstTurnInputKind.GuardedSessionNative,
            SingleAgentOrchestrator.ClassifyFirstTurnInput(input));

    // Casing must not defeat the guard.
    [Theory]
    [InlineData("/COMPACT")]
    [InlineData("/Undo")]
    public void Guard_IsCaseInsensitive(string input)
        => Assert.Equal(SingleAgentOrchestrator.FirstTurnInputKind.GuardedSessionNative,
            SingleAgentOrchestrator.ClassifyFirstTurnInput(input));

    // Plain text and UNKNOWN slash input keep current behavior: they go to the agent.
    [Theory]
    [InlineData("write me a haiku")]
    [InlineData("  leading whitespace goal")]
    [InlineData("/notacommand")]
    [InlineData("/frobnicate the widgets")]
    [InlineData("")]
    public void GoalsAndUnknownSlash_RemainGoals(string input)
        => Assert.Equal(SingleAgentOrchestrator.FirstTurnInputKind.Goal,
            SingleAgentOrchestrator.ClassifyFirstTurnInput(input));

    // A bare "!" has no command to run; it is not shell (falls through as a goal so the
    // orchestrator's usage hint path can handle it consistently with the meta-loop).
    [Fact]
    public void BareBang_IsGoal()
        => Assert.Equal(SingleAgentOrchestrator.FirstTurnInputKind.Goal,
            SingleAgentOrchestrator.ClassifyFirstTurnInput("!"));

    // Session-agnostic commands handled UPSTREAM by MetaCommandDispatch never reach the
    // classifier in practice, but if they did they are still session-native per the command
    // table (SessionOnly/Both) and must not leak either.
    [Theory]
    [InlineData("/kanban")]
    [InlineData("/daemon jobs")]
    [InlineData("/voice")]
    public void UpstreamMetaCommands_StillNeverLeak(string input)
        => Assert.NotEqual(SingleAgentOrchestrator.FirstTurnInputKind.Goal,
            SingleAgentOrchestrator.ClassifyFirstTurnInput(input));
}
