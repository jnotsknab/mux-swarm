using System;
using System.Collections.Generic;
using System.IO;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Tui;
using MuxSwarm.State;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>/createhook asks every question first, then hands one brief carrying the choices to a normal agent session.</summary>
[Collection("ConsoleState")]
public class CreateHookFlowTests
{
    private static string SourceRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));

    [Fact]
    public void NewScript_AgentStartsAfterEveryChoice_WithTheChoicesInItsBrief()
    {
        var field = typeof(PlatformContext).GetField("_swarmPathOverride", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var savedPath = field.GetValue(null);
        bool stdio = MuxConsole.StdioMode;
        var input = MuxConsole.InputOverride;
        var output = Console.Out;
        string dir = Path.Combine(Path.GetTempPath(), "mux-createhook-" + Guid.NewGuid().ToString("N"));
        string id = "createhook-flow-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(dir);
        string scriptPath = Path.Combine(PlatformContext.BaseDirectory, "Hooks", id + ".py");
        try
        {
            field.SetValue(null, Path.Combine(dir, "Swarm.json"));
            MuxConsole.StdioMode = true;
            int ev(string e) => Array.IndexOf(TuiConfigCommands.HookEvents, e) + 1;
            string python = PlatformContext.IsWindows ? "3" : "2";
            // kind=hook, source=new script, purpose, language, events, no agent filter, no tool filter, async
            var reader = new StringReader($"1\n1\nlog tool calls\n{python}\n{ev("tool_call")},{ev("turn_end")}\nn\nn\nn\n");
            MuxConsole.InputOverride = reader;
            Console.SetOut(new StringWriter());

            var calls = new List<(string Path, string Brief, bool Saved, int Unread)>();
            var result = TuiConfigCommands.RunCreateHookWizard(new[] { "/createhook", id },
                (path, brief) => calls.Add((path, brief, File.Exists(Path.Combine(dir, "Swarm.json")), reader.Peek())));

            Assert.True(result.Ok, result.Message);
            var call = Assert.Single(calls);
            Assert.Equal(-1, call.Unread);        // every question was answered before the agent was started
            Assert.True(call.Saved);              // and the hook entry already exists
            Assert.Equal(scriptPath, call.Path);
            Assert.Contains("fires on: tool_call, turn_end", call.Brief);
            Assert.Contains("log tool calls", call.Brief);
            Assert.Contains("## Event Hooks", call.Brief);
            Assert.Contains(Path.Combine(PlatformContext.ContextDirectory, "DOCS.md"), call.Brief);
            Assert.Contains(scriptPath, call.Brief);
        }
        finally
        {
            Console.SetOut(output);
            MuxConsole.StdioMode = stdio;
            MuxConsole.InputOverride = input;
            field.SetValue(null, savedPath);
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Menu_QueuesAnAgentSession_InsteadOfAFixedHelper()
    {
        // The draft runs as a normal /agent session (configured model, context window and compaction),
        // not a one-shot ChatAgentAsync with the default 80k threshold that echoed the brief.
        var app = File.ReadAllText(Path.Combine(SourceRoot(), "App.cs"));
        int start = app.IndexOf("case var hkCmd when hkCmd == \"/createhook\"", StringComparison.Ordinal);
        int end = app.IndexOf("default:", app.IndexOf("case var hksCmd when hksCmd == \"/hooks\"", StringComparison.Ordinal), StringComparison.Ordinal);
        var block = app[start..end];
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(block, @"CliCmdUtils\.QueueAgentSession\(brief\)").Count);
        Assert.DoesNotContain("ChatAgentAsync", block);
    }
}
