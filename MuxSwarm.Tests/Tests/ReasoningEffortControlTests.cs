using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using MuxSwarm.Utils;
using MuxSwarm.Utils.Tui;
using OpenAI;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

#pragma warning disable OPENAI001

namespace MuxSwarm.Tests.Tests;

public class ReasoningEffortControlTests
{
    [Theory]
    [InlineData("/max")]
    [InlineData("/effort xhigh")]
    [InlineData("/effort max")]
    [InlineData("/effort custom future")]
    public void EffortCommands_AreSessionNative(string command)
        => Assert.True(TuiCommands.IsSessionNative(command.Split(' ', 2)[0]));

    [Fact]
    public void Help_AdvertisesDistinctTiersAndCustomEscapeHatch()
    {
        Assert.Contains("/effort xhigh", Help.HelpText);
        Assert.Contains("/effort max", Help.HelpText);
        Assert.Contains("/effort custom", Help.HelpText);
        Assert.Contains("low/med/high/xhigh/max", Help.HelpText);
        Assert.True(TuiCommands.TakesArgument("/effort"));
        Assert.True(TuiCommands.TakesArgument("/effort custom"));
        Assert.False(TuiCommands.TakesArgument("/max"));
    }

    [Theory]
    [InlineData("xhigh", "xhigh", "xhigh")]
    [InlineData("extra_high", "xhigh", "xhigh")]
    [InlineData("ExtraHigh", "xhigh", "xhigh")]
    [InlineData("MAX", "max", "max")]
    [InlineData("custom Future-Level V2", "custom Future-Level V2", "Future-Level V2")]
    [InlineData("custom xhigh", "custom xhigh", "xhigh")]
    [InlineData("custom max", "custom max", "max")]
    [InlineData("custom Vendor\"Tier\\V2", "custom Vendor\"Tier\\V2", "Vendor\"Tier\\V2")]
    [InlineData("custom   Mixed  CASE  ", "custom Mixed  CASE", "Mixed  CASE")]
    [InlineData("low", "low", "low")]
    [InlineData("medium", "med", "medium")]
    [InlineData("high", "high", "high")]
    public async Task Selection_SerializesExactWireValue(string input, string label, string wire)
    {
        var options = Select(input);
        Assert.Equal(label, ReasoningEffortControl.GetLabel(options));
        using var transport = new CaptureHandler();
        using var client = CreateClient(transport);
        await client.GetResponseAsync(Messages, options.Clone());
        Assert.Equal(wire, Effort(Assert.Single(transport.Bodies)));
        Assert.DoesNotContain("mux.internal", transport.Bodies[0]);
    }

    [Theory]
    [InlineData("/max", "max", "max")]
    [InlineData("/effort max", "max", "max")]
    [InlineData("/effort xhigh", "xhigh", "xhigh")]
    [InlineData("/EFFORT custom MAX", "custom MAX", "MAX")]
    public async Task Commands_SetAndSendSelection(string command, string label, string wire)
    {
        var options = new ChatOptions();
        Assert.True(ReasoningEffortControl.TryHandleCommand(options, command, out var actual, out var error));
        Assert.Null(error);
        Assert.Equal(label, actual);
        using var transport = new CaptureHandler();
        using var client = CreateClient(transport);
        await client.GetResponseAsync(Messages, options);
        Assert.Equal(wire, Effort(Assert.Single(transport.Bodies)));
    }

    [Theory]
    [InlineData("/max nope")]
    [InlineData("/effort custom")]
    [InlineData("/effort custom   ")]
    [InlineData("/effort bogus")]
    [InlineData("/effort custom bad\nvalue")]
    public void InvalidCommands_LeaveSelectionUntouched(string command)
    {
        var options = Select("max");
        Assert.True(ReasoningEffortControl.TryHandleCommand(options, command, out var label, out var error));
        Assert.Null(label);
        Assert.NotNull(error);
        Assert.Equal("max", ReasoningEffortControl.GetLabel(options));
    }

    [Fact]
    public void UnrelatedCommand_IsNotConsumed()
    {
        var options = Select("max");
        Assert.False(ReasoningEffortControl.TryHandleCommand(options, "/maxp 3", out _, out _));
        Assert.Equal("max", ReasoningEffortControl.GetLabel(options));
    }

    [Fact]
    public async Task Cycle_ReachesMax_WrapsAndClearsRawOverride()
    {
        var options = new ChatOptions();
        using var transport = new CaptureHandler();
        using var client = CreateClient(transport);
        foreach (var label in new[] { "low", "med", "high", "xhigh", "max", "low" })
        {
            Assert.Equal(label, ReasoningEffortControl.Cycle(options));
            await client.GetResponseAsync(Messages, options);
        }
        Assert.Equal(new[] { "low", "medium", "high", "xhigh", "max", "low" }, transport.Bodies.Select(Effort));
        Assert.Null(ReasoningEffortControl.GetExplicit(options));
    }

    [Fact]
    public async Task CustomThenPreset_RetainsOptionsAndFactoryWithoutStaleCapture()
    {
        var options = new ChatOptions
        {
            MaxOutputTokens = 1234,
            Reasoning = new ReasoningOptions { Output = ReasoningOutput.Summary },
            AdditionalProperties = new() { ["unrelated"] = "kept" },
            RawRepresentationFactory = _ => new ChatCompletionOptions { FrequencyPenalty = 0.25f }
        };
        var factory = options.RawRepresentationFactory;
        ReasoningEffortControl.Apply(options, Parse("custom Future-Level"));
        var snapshot = options.Clone();
        Assert.Equal("low", ReasoningEffortControl.Cycle(options));
        Assert.Same(factory, options.RawRepresentationFactory);
        Assert.Equal(ReasoningOutput.Summary, options.Reasoning!.Output);
        Assert.Equal("kept", options.AdditionalProperties!["unrelated"]);
        using var transport = new CaptureHandler();
        using var client = CreateClient(transport);
        await client.GetResponseAsync(Messages, snapshot);
        await client.GetResponseAsync(Messages, options);
        Assert.Equal(new[] { "Future-Level", "low" }, transport.Bodies.Select(Effort));
        foreach (var body in transport.Bodies)
        {
            using var document = JsonDocument.Parse(body);
            Assert.Equal(0.25, document.RootElement.GetProperty("frequency_penalty").GetDouble());
            Assert.Equal(1234, document.RootElement.GetProperty("max_completion_tokens").GetInt32());
        }
    }

    [Theory]
    [InlineData("max")]
    [InlineData("custom future")]
    [InlineData("custom xhigh")]
    public async Task ExplicitRejection_IsNotRetriedOrLatched_EvenForClaude(string value)
    {
        using var transport = new CaptureHandler(reject: true);
        var model = "claude-test-" + Guid.NewGuid().ToString("N");
        using var client = CreateClient(transport, model);
        await Assert.ThrowsAsync<ClientResultException>(() => client.GetResponseAsync(Messages, Select(value)));
        Assert.Single(transport.Bodies);
        Assert.Equal(Parse(value).RawValue, Effort(transport.Bodies[0]));
        Assert.False(ReasoningEffortFallbackClient.IsDowngraded(model));
    }

    [Theory]
    [InlineData("max", false)]
    [InlineData("custom Vendor-Max", false)]
    [InlineData("max", true)]
    [InlineData("custom xhigh", true)]
    public async Task Streaming_UsesExactEffortAndDoesNotFallback(string value, bool reject)
    {
        using var transport = new CaptureHandler(reject, streaming: true);
        var model = "stream-" + Guid.NewGuid().ToString("N");
        using var client = CreateClient(transport, model);
        async Task Consume()
        {
            await foreach (var _ in client.GetStreamingResponseAsync(Messages, Select(value))) { }
        }
        if (reject) await Assert.ThrowsAsync<ClientResultException>(Consume);
        else await Consume();
        Assert.Equal(Parse(value).RawValue, Effort(Assert.Single(transport.Bodies)));
        Assert.False(ReasoningEffortFallbackClient.IsDowngraded(model));
    }

    [Theory]
    [InlineData("xhigh", "xhigh")]
    [InlineData("extra_high", "xhigh")]
    [InlineData("max", "max")]
    [InlineData("custom Vendor-V2", "custom Vendor-V2")]
    public void ConfigSelection_UsesSameMapping(string value, string label)
    {
        var options = new ModelOpts { Reasoning = new ReasoningConfig { Effort = value, Output = "full" } }.ToChatOptions()!;
        Assert.Equal(label, ReasoningEffortControl.GetLabel(options));
        Assert.Equal(ReasoningOutput.Full, options.Reasoning!.Output);
        Assert.Equal(label, ReasoningEffortControl.GetLabel(options.Clone()));
    }

    [Theory]
    [InlineData("max")]
    [InlineData("custom [Vendor] <raw>")]
    public void Footer_DisplaysAndEscapesSelection(string value)
    {
        var markup = TuiComponents.Footer(0, 1000, false, false, false, effort: value);
        var plain = Spectre.Console.Markup.Remove(markup);
        Assert.Contains("◐ " + value, plain);
    }

    [Fact]
    public async Task LatchedXhighFallback_DoesNotOverrideLaterExplicitMax()
    {
        var model = "latched-" + Guid.NewGuid().ToString("N");
        using var rejected = new CaptureHandler(reject: true);
        using (var client = CreateClient(rejected, model))
            await Assert.ThrowsAsync<ClientResultException>(() => client.GetResponseAsync(Messages, Select("xhigh")));
        Assert.True(ReasoningEffortFallbackClient.IsDowngraded(model));
        using var accepted = new CaptureHandler();
        using (var client = CreateClient(accepted, model))
            await client.GetResponseAsync(Messages, Select("max"));
        Assert.Equal("max", Effort(Assert.Single(accepted.Bodies)));
    }

    [Fact]
    public async Task UnsupportedNativeFactory_FailsInsteadOfIgnoringCustomEffort()
    {
        var options = Select("max");
        options.RawRepresentationFactory = _ => new object();
        using var transport = new CaptureHandler();
        using var client = CreateClient(transport);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Messages, options));
        Assert.Empty(transport.Bodies);
    }

    [Fact]
    public async Task FunctionInvocationMiddleware_PreservesMaxAcrossClonedRequests()
    {
        using var transport = new CaptureHandler(toolCall: true);
        using var client = CreateClient(transport).AsBuilder().UseFunctionInvocation().Build();
        var configured = new ModelOpts
        {
            Reasoning = new ReasoningConfig { Effort = "max", Output = "full" }
        }.ToChatOptions()!;
        var merged = new ChatOptions
        {
            Reasoning = configured.Reasoning,
            AdditionalProperties = configured.AdditionalProperties,
            Tools = [AIFunctionFactory.Create(() => "unused", "test_tool")]
        };
        await client.GetResponseAsync(Messages, merged.Clone());
        Assert.Equal(2, transport.Bodies.Count);
        Assert.All(transport.Bodies, body => Assert.Equal("max", Effort(body)));
        Assert.Equal("max", ReasoningEffortControl.GetLabel(merged));
        Assert.Null(merged.RawRepresentationFactory);
    }

    private static readonly ChatMessage[] Messages = [new(ChatRole.User, "local serialization test")];

    private static ReasoningEffortControl.Selection Parse(string value)
    {
        Assert.True(ReasoningEffortControl.TryParse(value, out var selection));
        return selection!;
    }

    private static ChatOptions Select(string value)
    {
        var options = new ChatOptions();
        ReasoningEffortControl.Apply(options, Parse(value));
        return options;
    }

    private static string Effort(string body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("reasoning_effort").GetString()!;
    }

    private static IChatClient CreateClient(CaptureHandler transport, string model = "gpt-6-astra")
    {
        var native = new OpenAIClient(new ApiKeyCredential("local-test-not-a-secret"), new OpenAIClientOptions
        {
            Endpoint = new Uri("https://serialization.invalid/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(transport)),
            RetryPolicy = new ClientRetryPolicy(0)
        }).GetChatClient(model).AsIChatClient();
        return new ReasoningEffortFallbackClient(native, model);
    }

    private sealed class CaptureHandler(bool reject = false, bool streaming = false, bool toolCall = false) : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (reject)
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"error\":{\"message\":\"unsupported reasoning_effort\",\"type\":\"invalid_request_error\",\"code\":\"unsupported_value\"}}", Encoding.UTF8, "application/json")
                };
            if (toolCall && Bodies.Count == 1)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"id":"chatcmpl-tool","object":"chat.completion","created":1,"model":"gpt-6-astra","choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call-local","type":"function","function":{"name":"test_tool","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}
                        """, Encoding.UTF8, "application/json")
                };
            const string completion = "{\"id\":\"chatcmpl-test\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"gpt-6-astra\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}";
            const string stream = "data: {\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-6-astra\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":null}]}\n\ndata: {\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-6-astra\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(streaming ? stream : completion, Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
            };
        }
    }
}
