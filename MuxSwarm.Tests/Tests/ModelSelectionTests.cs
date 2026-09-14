using System.Text.Json.Nodes;
using MuxSwarm.Engine;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

public class ModelSelectionTests
{
    private const string Config = """
        {"unknown":{"keep":true},"singleAgent":{"name":"Orchestrator","model":"old","extra":7,
         "modelOpts":{"temperature":0.7,"reasoning":{"effort":"high","output":"full","vendor":1}}},
         "orchestrator":{"model":"orch"},"agents":[{"name":"Orchestrator","model":"worker"}]}
        """;

    [Fact]
    public void Apply_PreservesUnknownFieldsAndUsesStableSlotId()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mux-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Swarm.json");
        try
        {
            File.WriteAllText(path, Config);
            var snap = ModelSelectionStore.Load(path);
            Assert.Equal(3, snap.Slots.Count);
            var updated = ModelSelectionStore.Save(snap, "agents/0", "new-model", "custom Future Level");
            Assert.Equal("new-model", updated.Agents[0].Model);
            Assert.Equal("custom Future Level", updated.Agents[0].ModelOpts!.Reasoning!.Effort);
            var root = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.True(root["unknown"]!["keep"]!.GetValue<bool>());
            Assert.Equal(7, root["singleAgent"]!["extra"]!.GetValue<int>());
            Assert.Equal("old", root["singleAgent"]!["model"]!.GetValue<string>());
            Assert.Single(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void DefaultEffort_RemovesOnlyEffortAndConflictsDoNotOverwrite()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mux-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Swarm.json");
        try
        {
            File.WriteAllText(path, Config);
            var snap = ModelSelectionStore.Load(path);
            ModelSelectionStore.Save(snap, "singleAgent", "new", null);
            var node = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.Null(node["singleAgent"]!["modelOpts"]!["reasoning"]!["effort"]);
            Assert.Equal("full", node["singleAgent"]!["modelOpts"]!["reasoning"]!["output"]!.GetValue<string>());
            byte[] saved = File.ReadAllBytes(path);
            Assert.Throws<IOException>(() => ModelSelectionStore.Save(snap, "singleAgent", "overwrite", "low"));
            Assert.Equal(saved, File.ReadAllBytes(path));
            Assert.Single(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void InvalidSelections_DoNotWrite()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mux-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Swarm.json");
        try
        {
            File.WriteAllText(path, Config);
            var snap = ModelSelectionStore.Load(path);
            Assert.Throws<ArgumentException>(() => ModelSelectionStore.Save(snap, "singleAgent", "", "high"));
            Assert.Throws<ArgumentException>(() => ModelSelectionStore.Save(snap, "singleAgent", "new", "custom "));
            Assert.Equal(snap.Bytes, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, char c = '\0', bool ctrl = false)
        => new(c, key, false, false, ctrl);
    private static ModelPickerView View() => new(new[]
    {
        new ModelSelectionStore.Slot("singleAgent", "Main", "old", "high"),
        new ModelSelectionStore.Slot("orchestrator", "Orchestrator", "other", null),
    }, "test-provider");

    [Fact]
    public void NavigationSearchAndEffort_AreStagedUntilExplicitApply()
    {
        var view = View();
        view.SetModels(new[] { "model-a", "model-b", "old" }, null);
        Assert.Equal(ModelPickerView.Action.None, view.Handle(Key(ConsoleKey.Enter), 5));
        view.Paste("model-b");
        view.Handle(Key(ConsoleKey.Enter), 5);
        Assert.Equal("model-b", view.Model);
        view.Handle(Key(ConsoleKey.RightArrow), 5);
        Assert.Equal("xhigh", view.Effort);
        Assert.Equal(ModelPickerView.Action.Apply, view.Handle(Key(ConsoleKey.F4), 5));
        Assert.Equal("old", view.Slot!.Model);
    }

    [Fact]
    public void CancelRefreshAndManualFallback_DoNotImplicitlyApply()
    {
        var view = View();
        Assert.Equal(ModelPickerView.Action.Refresh, view.Handle(Key(ConsoleKey.F5), 5));
        view.SetModels([], "HTTP 404");
        view.Handle(Key(ConsoleKey.Enter), 5);
        view.Handle(Key(ConsoleKey.F2), 5);
        view.Paste("-manual\n");
        Assert.Equal("old-manual", view.Model);
        Assert.Equal(ModelPickerView.Action.Cancel, view.Handle(Key(ConsoleKey.Q, '\u0011', true), 5));
    }

    [Theory]
    [InlineData(20, 8)]
    [InlineData(40, 12)]
    [InlineData(120, 30)]
    public void Layout_IsExactlyViewportAndNeverWraps(int width, int height)
    {
        var view = View();
        view.SetModels(Enumerable.Range(0, 1000).Select(i => "model-" + i + "-" + new string('x', 200)).ToArray(), null);
        for (int pane = 0; pane < 3; pane++)
        {
            var rows = view.Render(width, height);
            Assert.Equal(height, rows.Count);
            Assert.All(rows, row => Assert.InRange(TuiMarkup.MarkupWidth(row), 0, width));
            view.Handle(Key(ConsoleKey.Tab), height - 10);
        }
    }
    [Fact]
    public void ApplyFromModelList_UsesHighlightedMatchNotPreviousModel()
    {
        var view = View();
        view.SetModels(new[] { "new-model", "old" }, null);
        view.Handle(Key(ConsoleKey.Enter), 5);
        view.Paste("new-");
        Assert.Equal(ModelPickerView.Action.Apply, view.Handle(Key(ConsoleKey.F4), 5));
        Assert.Equal("new-model", view.Model);
    }

    [Theory]
    [InlineData("medium", "high")]
    [InlineData("extra_high", "max")]
    public void ExistingEffortAliases_CycleFromCanonicalTier(string stored, string expected)
    {
        var view = new ModelPickerView(new[] { new ModelSelectionStore.Slot("singleAgent", "Main", "old", stored) }, "provider");
        view.Handle(Key(ConsoleKey.Tab), 5);
        view.Handle(Key(ConsoleKey.Tab), 5);
        view.Handle(Key(ConsoleKey.RightArrow), 5);
        Assert.Equal(expected, view.Effort);
    }

    [Fact]
    public void EndOfCatalog_SelectedModelRemainsVisible()
    {
        var view = View();
        view.SetModels(Enumerable.Range(0, 100).Select(i => $"model-{i:000}").ToArray(), null);
        view.Handle(Key(ConsoleKey.Enter), 5);
        view.Handle(Key(ConsoleKey.End), 5);
        Assert.Contains(view.Render(80, 20), row => TuiMarkup.Plain(row).Contains("› model-099"));
    }

}
