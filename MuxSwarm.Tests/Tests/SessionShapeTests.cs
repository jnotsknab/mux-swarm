using System.Text.Json;
using Microsoft.Extensions.AI;
using MuxSwarm.Utils;

namespace MuxSwarm.Tests.Tests;

// Regression coverage for the session-shape mismatch bug: the serialized session uses
// stateBag.InMemoryChatHistoryProvider.messages, but several readers historically looked
// for chatHistoryProviderState.messages and therefore always returned empty (no preview,
// "Extracted 0 messages", broken resumed /undo + /retry + compaction bookkeeping).
public class SessionShapeTests
{
    // The real serialized shape produced by Microsoft.Agents.AI.
    private const string CurrentShape = """
    {
      "stateBag": {
        "InMemoryChatHistoryProvider": {
          "messages": [
            { "role": "user", "contents": [ { "$type": "text", "text": "what have we been working on" } ] },
            { "authorName": "Companion", "role": "assistant", "contents": [ { "$type": "text", "text": "the interrupt bug" } ] }
          ]
        }
      }
    }
    """;

    // The legacy shape that the fallback branch must still tolerate.
    private const string LegacyShape = """
    {
      "chatHistoryProviderState": {
        "messages": [
          { "role": "user", "contents": [ { "$type": "text", "text": "legacy goal" } ] }
        ]
      }
    }
    """;

    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void TryGetSessionMessages_CurrentShape_Resolves()
    {
        Assert.True(Common.TryGetSessionMessages(Root(CurrentShape), out var msgs));
        Assert.Equal(2, msgs.GetArrayLength());
    }

    [Fact]
    public void TryGetSessionMessages_LegacyShape_Resolves()
    {
        Assert.True(Common.TryGetSessionMessages(Root(LegacyShape), out var msgs));
        Assert.Equal(1, msgs.GetArrayLength());
    }

    [Fact]
    public void TryGetSessionMessages_UnknownShape_ReturnsFalse()
    {
        Assert.False(Common.TryGetSessionMessages(Root("""{ "foo": 1 }"""), out _));
    }

    [Fact]
    public void ExtractMessagesFromSession_CurrentShape_ReturnsAllMessages()
    {
        var msgs = Common.ExtractMessagesFromSession(Root(CurrentShape));
        Assert.Equal(2, msgs.Count);
        Assert.Equal(ChatRole.User, msgs[0].Role);
        Assert.Contains("working on", msgs[0].Text);
        Assert.Equal(ChatRole.Assistant, msgs[1].Role);
        Assert.Equal("the interrupt bug", msgs[1].Text);
    }

    [Fact]
    public void GetFirstUserMessage_CurrentShape_ReturnsPreview()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mux_sesh_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, CurrentShape);
            var preview = Common.GetFirstUserMessage(path);
            Assert.Contains("what have we been working on", preview);
            Assert.NotEqual("No preview", preview);
        }
        finally { File.Delete(path); }
    }
}
