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

    private static readonly FieldInfo PendingField = typeof(HookWorker).GetField("_confirmPending", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly FieldInfo CtsField = typeof(HookWorker).GetField("_cts", BindingFlags.NonPublic | BindingFlags.Static)!;

    // Interactive start with one hook that never fires: (prompt queued for after the TUI is up, hook loop running, Enabled).
    private static (bool Pending, bool Running, bool Enabled) StartWithOneHook(bool suppress)
    {
        var priorConfig = App.Config; var priorHooks = HooksField.GetValue(null);
        bool priorEnabled = HookWorker.Enabled, priorStdio = MuxConsole.StdioMode;
        try
        {
            App.Config = new AppConfig { Console = new ConsoleConfig { SuppressActionWarnings = suppress } };
            MuxConsole.StdioMode = false;
            HookWorker.Stop(); PendingField.SetValue(null, false); HookWorker.Enabled = true;
            HookWorker.Start([System.Text.Json.JsonSerializer.Deserialize<HookConfig>("{\"id\":\"never\",\"command\":\"echo\",\"when\":{\"event\":\"no_such_event\"}}")!]);
            return ((bool)PendingField.GetValue(null)!, CtsField.GetValue(null) is not null, HookWorker.Enabled);
        }
        finally
        {
            PendingField.SetValue(null, false); HookWorker.Stop();
            HooksField.SetValue(null, priorHooks); HookWorker.Enabled = priorEnabled;
            App.Config = priorConfig; MuxConsole.StdioMode = priorStdio;
        }
    }

    [Fact]
    public void Suppressed_SkipsTheHooksPrompt_AndKeepsHooksEnabled()
    {
        var r = StartWithOneHook(suppress: true);
        Assert.False(r.Pending);   // no "Proceed with hooks enabled?" queued
        Assert.True(r.Running);    // hooks started right away
        Assert.True(r.Enabled);
    }

    [Fact]
    public void Default_StillAsks()
    {
        Assert.False(new ConsoleConfig().SuppressActionWarnings);
        var r = StartWithOneHook(suppress: false);
        Assert.True(r.Pending);    // asked once the TUI is up (HookWorker.ConfirmPending)
        Assert.False(r.Running);
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
