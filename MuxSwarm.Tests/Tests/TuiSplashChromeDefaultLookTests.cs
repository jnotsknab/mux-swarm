using System.Security.Cryptography;
using System.Text;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Tui;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>The splash at every breakpoint and the full-screen pickers keep the stock look with no styles
/// set. Hashes recorded on the pre-change code (1ca7758) from this same script.</summary>
[Collection("ConsoleState")]
public class TuiSplashChromeDefaultLookTests
{
    internal static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.Replace("\r\n", "\n"))))[..16];

    internal static string RenderSplash()
    {
        var prior = Theme.Active;
        var sb = new StringBuilder();
        try
        {
            Theme.Set(Theme.Default);
            var recent = new List<MuxConsole.SplashSession>
            {
                new("2026-10-20_17-52-09", "agent", "fix the login bug"),
                new("2026-10-19_09-00-00", "swarm", "write the release notes for the nightly build"),
            };
            foreach (int w in new[] { 40, 120, 200, 260 })
                foreach (var l in MuxConsole.BuildFrameSplashLines("1.2.3", "", "Tip", "hello there", w, recent))
                    sb.Append(TuiMarkup.ToAnsi(l)).Append('\n');
        }
        finally { Theme.Set(prior); }
        return sb.ToString();
    }

    [Fact]
    public void Splash_NoStyles_IsUnchanged() => Assert.Equal("3ACA2AE68D0DDACA", Hash(RenderSplash()));

    /// <summary>Every full-screen view (pickers, history, /extensions, an extension panel) at a fixed size.</summary>
    internal static string RenderViews()
    {
        var prior = Theme.Active;
        var surface = Engine.Extensions.ExtensionPanels.Surface;
        var sb = new StringBuilder();
        void Add(IEnumerable<string> rows) { foreach (var r in rows) sb.Append(TuiMarkup.ToAnsi(r)).Append('\n'); }
        try
        {
            Theme.Set(Theme.Default);
            Add(new AgentPickerView([AgentPickerViewTests.Agent("CodeAgent", "writes code"), AgentPickerViewTests.Agent("WebAgent", "browses")], "CodeAgent").Render(90, 20));
            Add(new ThemePickerView(activeName: "nord").Render(100, 22));
            Add(new SettingsPickerView([SettingsPickerViewTests.Item("collapseToolLines"), SettingsPickerViewTests.Item("theme", "nord", "<name>")]).Render(90, 20));
            var model = new ModelPickerView([new Engine.ModelSelectionStore.Slot("singleAgent", "Main", "old-model", "high")], "provider");
            model.SetModels(["model-a", "model-b"], null);
            Add(model.Render(90, 22));
            Add(new HistoryView([("2026-10-20_17-52-09", "fix the login bug", "release"), ("2026-10-19_09-00-00", "notes", null)], resumePicker: true).Render(90, 20));
            Add(new ExtensionsPanelView([ExtensionsPanelTests.Item("ember"), ExtensionsPanelTests.Item("meter", process: true, trusted: true)]).Render(110, 24));
            Engine.Extensions.ExtensionPanels.Surface = () => null;
            using var doc = System.Text.Json.JsonDocument.Parse("""
                { "type": "box", "children": [ { "type": "text", "text": "Notes" }, { "type": "input", "key": "new", "placeholder": "add" },
                  { "type": "list", "key": "notes", "items": [ { "value": "1", "label": "buy milk" }, { "value": "2", "label": "ship it" } ] },
                  { "type": "button", "key": "clear", "label": "Clear", "hotkey": "c" } ] }
                """);
            Engine.Extensions.ExtensionPanels.Open("ref", "screen", "Notes", doc.RootElement.Clone(), "screen");
            Add(new ExtensionPanelView(Engine.Extensions.ExtensionPanels.Get("ref", "screen")!).Render(80, 18));
        }
        finally
        {
            foreach (var p in Engine.Extensions.ExtensionPanels.All()) Engine.Extensions.ExtensionPanels.Close(p.Extension, p.Id, byUser: false);
            Engine.Extensions.ExtensionPanels.Surface = surface;
            Theme.Set(prior);
        }
        return sb.ToString();
    }

    [Fact]
    public void Views_NoStyles_AreUnchanged() => Assert.Equal("88F714EE36C302EF", Hash(RenderViews()));
}
