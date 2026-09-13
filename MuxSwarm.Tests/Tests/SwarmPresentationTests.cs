using System.Collections;
using System.Reflection;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>Swarm presentation tests with a fake terminal, never live stdin or provider calls.</summary>
[Collection("ConsoleState")]
public class SwarmPresentationTests
{
    private sealed class Terminal : ITuiTerminal
    {
        public int Width { get; init; } = 60;
        public int Height => 24;
        public void Write(string text) { }
        public void Flush() { }
    }

    private sealed class ConsoleScope : IDisposable
    {
        private readonly Dictionary<FieldInfo, object?> _saved;
        private readonly bool _stdio = MuxConsole.StdioMode;
        private readonly string _mode = ServeMode.ActiveMode;
        public TuiDriver Driver { get; }
        public ConsoleScope(bool frame, int width = 60)
        {
            var names = new[] { "_driver", "_tuiActive", "_interactiveRenderMode", "_fTokens", "_fThreshold",
                "_fCached", "_fPlan", "_fUltra", "_fPsub", "_fSub", "_fGiga" };
            _saved = names.Select(n => typeof(MuxConsole).GetField(n, BindingFlags.NonPublic | BindingFlags.Static)!)
                .ToDictionary(f => f, f => f.GetValue(null));
            Driver = new TuiDriver(new Terminal { Width = width }, frameEngine: frame);
            foreach (var field in _saved.Keys)
            {
                if (field.Name == "_driver") field.SetValue(null, Driver);
                if (field.Name == "_tuiActive") field.SetValue(null, true);
                if (field.Name == "_interactiveRenderMode") field.SetValue(null, RenderMode.Tui);
            }
            MuxConsole.StdioMode = false;
            ServeMode.ActiveMode = "swarm";
        }
        public void Dispose()
        {
            foreach (var entry in _saved) entry.Key.SetValue(null, entry.Value);
            MuxConsole.StdioMode = _stdio;
            ServeMode.ActiveMode = _mode;
        }
    }

    private static object LastEntry(TuiDriver driver)
        => ((IEnumerable)typeof(TuiDriver).GetField("_transcript", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(driver)!).Cast<object>().Last();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Completion_DoesNotRetintFollowingOutputAsAnAgent(bool frame)
    {
        using var scope = new ConsoleScope(frame);
        scope.Driver.SetLaneTint("#123456");
        MuxConsole.RenderTuiTaskComplete("Task", "done");
        var tint = typeof(TuiDriver).GetField("_laneTint", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(scope.Driver);
        Assert.Equal("#123456", tint);
    }

    [Theory]
    [InlineData(false, 45)]
    [InlineData(true, 45)]
    [InlineData(false, 180)]
    [InlineData(true, 180)]
    public void LongCompletion_IsOneCompactRowWithFullSummaryRetained(bool frame, int width)
    {
        using var scope = new ConsoleScope(frame, width);
        string summary = new string('x', 180) + "\nunique result 漢字 [literal]\nEND-SENTINEL";
        MuxConsole.RenderTuiTaskComplete("Task", summary);
        object entry = LastEntry(scope.Driver);
        string row = (string)entry.GetType().GetField("Collapsed")!.GetValue(entry)!;
        Assert.Single(LiveRegion.WrapMarkupLine(row, scope.Driver.Width));
        Assert.Contains("ctrl+e", TuiMarkup.Plain(row));
        var expanded = entry.GetType().GetField("Expandable")!.GetValue(entry);
        Assert.NotNull(expanded);
        Assert.Equal(summary, (((string Tool, string Text, bool Error))expanded!).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShortCompletion_IsReadableAndDoesNotCreateAnEmptyCard(bool frame)
    {
        using var scope = new ConsoleScope(frame);
        MuxConsole.RenderTuiTaskComplete("Task", "All checks passed.");
        object entry = LastEntry(scope.Driver);
        string row = (string)entry.GetType().GetField("Collapsed")!.GetValue(entry)!;
        Assert.Contains("All checks passed.", TuiMarkup.Plain(row));
        Assert.DoesNotContain("ctrl+e", TuiMarkup.Plain(row));
        Assert.Null(entry.GetType().GetField("Expandable")!.GetValue(entry));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(32)]
    [InlineData(60)]
    [InlineData(180)]
    public void CompletionSummary_UsesDisplayWidthAndEscapesLiteralMarkup(int width)
    {
        var result = TuiComponents.TaskCompleteSummary("Worker 漢字 [literal]", string.Concat(Enumerable.Repeat("👩‍💻 e\u0301 [red] ", 50)), width);
        Assert.True(result.Expandable);
        Assert.InRange(TuiMarkup.MarkupWidth(result.Markup), 1, width);
        Assert.Single(LiveRegion.WrapMarkupLine(result.Markup, width));
        Assert.Contains("ctrl+e", TuiMarkup.Plain(result.Markup));
    }

    [Fact]
    public void CompletionSummary_TinyWidthsNeverOverflowForHint()
    {
        for (int width = 1; width <= 19; width++)
        {
            var row = TuiComponents.TaskCompleteSummary("Task", "long result with details", width);
            Assert.True(row.Expandable);
            Assert.InRange(TuiMarkup.MarkupWidth(row.Markup), 1, width);
        }
    }

    [Fact]
    public void NonDockedCompletion_DoesNotDiscardLongSummary()
    {
        string summary = new string('x', 200) + "\nUNIQUE-END";
        string text = string.Join("\n", TuiComponents.TaskComplete("Task", summary).Select(TuiMarkup.Plain));
        Assert.Contains(summary, text);
    }

    [Theory]
    [InlineData("swarm", false)]
    [InlineData("swarm", true)]
    [InlineData("pswarm", false)]
    [InlineData("pswarm", true)]
    public void GoalFooter_HasModelAndWorkUsageWithoutInventingContextPercent(string mode, bool frame)
    {
        using var scope = new ConsoleScope(frame, 120);
        ServeMode.ActiveMode = mode;
        // A reused driver may still have the preceding single-agent session's counters.
        MuxConsole.SetTuiTokenBreakdown(1200, 800);
        MuxConsole.SetTuiToolCalls(17);
        MuxConsole.SetTuiModel("provider/test-model");
        MuxConsole.SetTuiTokenBreakdown(0, 0);
        MuxConsole.SetTuiToolCalls(0);
        MuxConsole.ResetTuiTurnClock();
        MuxConsole.StartTuiTurnClock();
        MuxConsole.UpdateDockedFooter(34829, 0, false, false, false);
        MuxConsole.StopTuiTurnClock();
        string footer = string.Join("\n", scope.Driver.BuildLiveFrame(scope.Driver.Width).Select(TuiMarkup.Plain));
        Assert.Contains(mode, footer);
        Assert.Contains("test-model", footer);
        Assert.Contains("34.8k tokens", footer);
        Assert.DoesNotContain('%', footer);
        Assert.DoesNotContain("sys", footer);
        Assert.DoesNotContain("tools", footer);
        Assert.DoesNotContain("calls", footer);
    }

    [Fact]
    public void TaskCompletion_StdioPayloadAndSuccessEventStayIntact()
    {
        bool previous = MuxConsole.StdioMode;
        var output = Console.Out;
        using var capture = new StringWriter();
        try
        {
            MuxConsole.StdioMode = true;
            Console.SetOut(capture);
            string summary = new string('x', 180) + "\nunique [literal]";
            MuxConsole.WriteTaskComplete("Task", summary);
            // Same gate as both orchestrators: true for stdio because IsTui is false.
            if (!MuxConsole.IsTui) MuxConsole.WriteSuccess("Orchestrator reports task complete.");
            var events = capture.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => System.Text.Json.JsonDocument.Parse(line)).ToList();
            try
            {
                var complete = events.Single(e => e.RootElement.GetProperty("type").GetString() == "task_complete");
                Assert.Equal(summary, complete.RootElement.GetProperty("summary").GetString());
                Assert.Contains(events, e => e.RootElement.GetProperty("type").GetString() == "success");
            }
            finally { foreach (var e in events) e.Dispose(); }
        }
        finally { Console.SetOut(output); MuxConsole.StdioMode = previous; }
    }

    [Theory]
    [InlineData("MultiAgentOrchestrator.cs")]
    [InlineData("ParallelSwarmOrchestrator.cs")]
    public void Orchestrators_UsePresentationGuardsAndPreserveCompletionState(string name)
    {
        // Call-site contract without provider/session/filesystem execution. Driver methods are exercised above.
        string text = File.ReadAllText(Path.Combine(SourceRoot(), "Engine", name));
        Assert.Contains("if (!MuxConsole.IsTui) MuxConsole.WriteSuccess(\"Orchestrator reports task complete.\");", text);
        Assert.Contains("if (!MuxConsole.IsTui) MuxConsole.WriteLine();\n            cancellationToken.ThrowIfCancellationRequested();\n            string response = responseText.ToString();", text.Replace("\r\n", "\n"));
        Assert.Contains("if (!MuxConsole.IsTui) MuxConsole.WriteRule();", text);
        Assert.Contains("goalComplete = true;", text);
        Assert.Contains("return \"Task marked as complete.\";", text);
        Assert.Contains("MuxConsole.WriteWarning($\"Task {status}: {summary}\");", text);
        Assert.Contains("MuxConsole.WriteMuted($\"  Artifacts: {artifacts}\");", text);
        Assert.Contains("MuxConsole.SetTuiModel(_orchestratorModelId);", text);
        Assert.Contains("MuxConsole.SetTuiTokenBreakdown(0, 0);", text);
        Assert.Contains("MuxConsole.SetTuiToolCalls(0);", text);
        Assert.Contains("MuxConsole.UpdateDockedFooter(_swarmTokens, 0,", text);
        Assert.Contains("MuxConsole.StopTuiTurnClock();", text);
        Assert.Contains("MuxConsole.SetTuiModel(null);", text);
    }

    private static string SourceRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));
}
