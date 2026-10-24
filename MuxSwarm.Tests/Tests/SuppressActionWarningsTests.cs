using System.Reflection;
using MuxSwarm.Engine;
using MuxSwarm.State;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>console.suppressActionWarnings: the startup hooks warning/prompt is skipped and hooks stay enabled.</summary>
[Collection("ConsoleState")]
public class SuppressActionWarningsTests
{
    private static readonly FieldInfo HooksField = typeof(HookWorker).GetField("_hooks", BindingFlags.NonPublic | BindingFlags.Static)!;

    // Starts the hook worker with one hook that never fires and returns what Start printed.
    private static string StartWithOneHook(bool suppress, out bool enabledAfter)
    {
        var priorConfig = App.Config; var priorHooks = HooksField.GetValue(null);
        bool priorEnabled = HookWorker.Enabled, priorStdio = MuxConsole.StdioMode;
        var priorOut = Console.Out; var sink = new StringWriter();
        try
        {
            App.Config = new AppConfig { Console = new ConsoleConfig { SuppressActionWarnings = suppress } };
            MuxConsole.StdioMode = true; Console.SetOut(sink);
            HookWorker.Stop(); HookWorker.Enabled = true;
            HookWorker.Start([System.Text.Json.JsonSerializer.Deserialize<HookConfig>("{\"id\":\"never\",\"command\":\"echo\",\"when\":{\"event\":\"no_such_event\"}}")!]);
            enabledAfter = HookWorker.Enabled;
            return sink.ToString();
        }
        finally
        {
            HookWorker.Stop();
            HooksField.SetValue(null, priorHooks); HookWorker.Enabled = priorEnabled;
            App.Config = priorConfig; MuxConsole.StdioMode = priorStdio; Console.SetOut(priorOut);
        }
    }

    [Fact]
    public void Suppressed_SkipsTheHooksWarning_AndKeepsHooksEnabled()
    {
        string shown = StartWithOneHook(suppress: true, out bool enabled);
        Assert.DoesNotContain("[Hooks]", shown);
        Assert.True(enabled);
    }

    [Fact]
    public void Default_StillWarns()
    {
        Assert.False(new ConsoleConfig().SuppressActionWarnings);
        string shown = StartWithOneHook(suppress: false, out _);
        Assert.Contains("[Hooks] 1 hook(s) configured", shown);
    }

    [Fact]
    public void Gate_OnlyWarnsWithHooksAndTheFlagOff()
    {
        var prior = App.Config;
        try
        {
            App.Config = new AppConfig();
            Assert.True(HookWorker.WarnsAtStartup(2));
            Assert.False(HookWorker.WarnsAtStartup(0));
            App.Config = new AppConfig { Console = new ConsoleConfig { SuppressActionWarnings = true } };
            Assert.False(HookWorker.WarnsAtStartup(2));
        }
        finally { App.Config = prior; }
    }

    [Fact]
    public void Flag_RoundTripsInConfigJson_AndIsOffWhenMissing()
    {
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<AppConfig>("{\"console\":{}}")!.Console.SuppressActionWarnings);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<AppConfig>("{\"console\":{\"suppressActionWarnings\":true}}")!.Console.SuppressActionWarnings);
        Assert.Contains("\"suppressActionWarnings\":true",
            System.Text.Json.JsonSerializer.Serialize(new AppConfig { Console = new ConsoleConfig { SuppressActionWarnings = true } }));
    }
}
