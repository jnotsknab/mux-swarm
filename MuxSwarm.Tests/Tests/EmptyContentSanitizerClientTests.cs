using Microsoft.Extensions.AI;
using MuxSwarm.Utils;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Covers EmptyContentSanitizerClient: it strips empty/whitespace-only TextContent parts from the
/// OUTBOUND history (fixing Kimi/Moonshot 400 "text content is empty" on replayed assistant turns) while
/// preserving all other content, never dropping a message, and never mutating the caller's list.
/// </summary>
public class EmptyContentSanitizerClientTests
{
    // Fake inner client: captures the exact message list it was asked to send.
    private sealed class CapturingInner : IChatClient
    {
        public IEnumerable<ChatMessage>? Seen { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Seen = messages;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Seen = messages;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static List<ChatMessage> Sent(CapturingInner inner) =>
        new(inner.Seen!);

    [Fact]
    public async Task StripsLeadingEmptyTextPart_TheExactKimiBugShape()
    {
        // [text:""], [text:"real"] — the captured failing assistant turn.
        var assistant = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new TextContent(""),
            new TextContent("I\u2019m Kimi, running as Mux."),
        });
        var history = new[]
        {
            new ChatMessage(ChatRole.User, "who are you"),
            assistant,
            new ChatMessage(ChatRole.User, "whats the system time"),
        };

        var inner = new CapturingInner();
        var client = new EmptyContentSanitizerClient(inner);
        await client.GetResponseAsync(history);

        var sent = Sent(inner);
        Assert.Equal(3, sent.Count);
        var a = sent[1];
        Assert.Single(a.Contents);
        Assert.Equal("I\u2019m Kimi, running as Mux.", ((TextContent)a.Contents[0]).Text);
    }

    [Fact]
    public async Task CleanHistory_PassesSameReference_NoAllocation()
    {
        var history = new[]
        {
            new ChatMessage(ChatRole.User, "hi"),
            new ChatMessage(ChatRole.Assistant, "hello"),
        };
        var inner = new CapturingInner();
        var client = new EmptyContentSanitizerClient(inner);
        await client.GetResponseAsync(history);

        // Nothing to strip -> the very same enumerable instance is forwarded.
        Assert.Same(history, inner.Seen);
    }

    [Fact]
    public async Task WhitespaceOnlyTextPart_IsStripped()
    {
        var assistant = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new TextContent("   \n\t "),
            new TextContent("real"),
        });
        var inner = new CapturingInner();
        var client = new EmptyContentSanitizerClient(inner);
        await client.GetResponseAsync(new[] { assistant });

        var a = Sent(inner)[0];
        Assert.Single(a.Contents);
        Assert.Equal("real", ((TextContent)a.Contents[0]).Text);
    }

    [Fact]
    public async Task AllEmptyMessage_IsPreserved_WithSingleEmptyPart()
    {
        // A turn that is ONLY empty text must not vanish (role/turn structure preserved).
        var assistant = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new TextContent(""),
            new TextContent("  "),
        });
        var inner = new CapturingInner();
        var client = new EmptyContentSanitizerClient(inner);
        await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi"), assistant });

        var sent = Sent(inner);
        Assert.Equal(2, sent.Count);
        Assert.Equal(ChatRole.Assistant, sent[1].Role);
        Assert.Single(sent[1].Contents);
        Assert.Equal("", ((TextContent)sent[1].Contents[0]).Text);
    }

    [Fact]
    public async Task NonTextContent_IsPreservedVerbatim()
    {
        // Reasoning + function call parts must survive; only the empty text is dropped.
        var fcall = new FunctionCallContent("call-1", "do_thing", new Dictionary<string, object?> { ["x"] = 1 });
        var assistant = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new TextContent(""),
            new TextReasoningContent("thinking..."),
            fcall,
        });
        var inner = new CapturingInner();
        var client = new EmptyContentSanitizerClient(inner);
        await client.GetResponseAsync(new[] { assistant });

        var a = Sent(inner)[0];
        Assert.Equal(2, a.Contents.Count);
        Assert.IsType<TextReasoningContent>(a.Contents[0]);
        Assert.Same(fcall, a.Contents[1]);
    }

    [Fact]
    public async Task DoesNotMutateCallerMessages()
    {
        var assistant = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new TextContent(""),
            new TextContent("real"),
        });
        var inner = new CapturingInner();
        var client = new EmptyContentSanitizerClient(inner);
        await client.GetResponseAsync(new[] { assistant });

        // Original message still has BOTH parts; only the forwarded copy was trimmed.
        Assert.Equal(2, assistant.Contents.Count);
    }

    [Fact]
    public async Task PreservesAuthorAndMessageId_OnRebuiltMessage()
    {
        var assistant = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new TextContent(""),
            new TextContent("real"),
        })
        {
            AuthorName = "MuxAgent",
            MessageId = "chatcmpl-xyz",
        };
        var inner = new CapturingInner();
        var client = new EmptyContentSanitizerClient(inner);
        await client.GetResponseAsync(new[] { assistant });

        var a = Sent(inner)[0];
        Assert.Equal("MuxAgent", a.AuthorName);
        Assert.Equal("chatcmpl-xyz", a.MessageId);
    }

    [Fact]
    public async Task StreamingPath_AlsoSanitizes()
    {
        var assistant = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new TextContent(""),
            new TextContent("real"),
        });
        var inner = new CapturingInner();
        var client = new EmptyContentSanitizerClient(inner);
        await foreach (var _ in client.GetStreamingResponseAsync(new[] { assistant })) { }

        var a = Sent(inner)[0];
        Assert.Single(a.Contents);
        Assert.Equal("real", ((TextContent)a.Contents[0]).Text);
    }
}
