using MuxSwarm.Engine;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// The model wrote its answer to a user's question only in its thinking (which the user never sees) and then asked
/// "does that make sense?". The ask_user description tells it where the user can actually read an answer.
/// </summary>
public class AskUserDescriptionTests
{
    [Fact]
    public void Description_SaysTheUserNeverSeesThinking_AndAnswersGoInTheQuestion()
    {
        var d = LocalAiFunctions.AskUserTool.Description;
        Assert.Contains("never your thinking", d);
        Assert.Contains("put that answer at the top of 'question'", d);
        Assert.Contains("CUSTOM", d);   // the existing bailout guidance stays
    }
}
