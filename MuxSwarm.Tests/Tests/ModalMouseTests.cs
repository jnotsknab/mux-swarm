using MuxSwarm.Engine;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>Batch 3 modal mouse support: view-level hit-region registration, the shared
/// MouseClickTracker capture contract, and full modal-loop click dispatch on the unstarted pump.
/// Clicks are an alias for the keyboard path - every assertion pins that equivalence.</summary>
[Collection("ConsoleState")]
public class ModalMouseTests
{
    private sealed class Terminal : ITuiTerminal
    {
        public int Width { get; set; } = 90;
        public int Height { get; set; } = 24;
        public System.Text.StringBuilder Output { get; } = new();
        public void Write(string text) => Output.Append(text);
        public void Flush() { }
    }

    private static ConsoleInputPump.InputEvent Key(ConsoleKey key)
        => ConsoleInputPump.InputEvent.OfKey(new ConsoleKeyInfo('\0', key, false, false, false));
    private static ConsoleInputPump.InputEvent Press(int col, int row)
        => ConsoleInputPump.InputEvent.OfMouse(0, col, row, false);
    private static ConsoleInputPump.InputEvent Release(int col, int row)
        => ConsoleInputPump.InputEvent.OfMouse(0, col, row, true);

    // ---- MouseClickTracker ----

    [Fact]
    public void ClickTracker_PressReleaseInRegion_IsClick_WithLocalCoords()
    {
        var map = new MouseHitMap();
        map.Begin(80, 24);
        map.Add(new HitRegion(5, 1, 3, 80, MouseTargetKind.PickerItem, 7));
        var t = new MouseClickTracker();
        Assert.False(t.Feed(Press(10, 6), map, out _, out _, out _, out _));
        Assert.True(t.Feed(Release(12, 7), map, out var hit, out int lr, out int lc, out int dbl));
        Assert.Equal(7, hit.Payload);
        Assert.Equal(2, lr);
        Assert.Equal(11, lc);
        Assert.Equal(1, dbl);
    }

    [Fact]
    public void ClickTracker_ReleaseOutsideRegion_IsDragCancel()
    {
        var map = new MouseHitMap();
        map.Begin(80, 24);
        map.Add(new HitRegion(5, 1, 1, 80, MouseTargetKind.PickerItem, 3));
        var t = new MouseClickTracker();
        Assert.False(t.Feed(Press(10, 5), map, out _, out _, out _, out _));
        Assert.False(t.Feed(Release(10, 9), map, out _, out _, out _, out _));
        // Capture cleared: a lone release later never clicks.
        Assert.False(t.Feed(Release(10, 5), map, out _, out _, out _, out _));
    }

    [Fact]
    public void ClickTracker_MotionNeverCaptures_PressOutsideNeverClicks()
    {
        var map = new MouseHitMap();
        map.Begin(80, 24);
        map.Add(new HitRegion(5, 1, 1, 80, MouseTargetKind.PickerItem, 3));
        var t = new MouseClickTracker();
        Assert.False(t.Feed(ConsoleInputPump.InputEvent.OfMouse(0x20, 10, 5, false), map, out _, out _, out _, out _));
        Assert.False(t.Feed(Release(10, 5), map, out _, out _, out _, out _));   // motion did not capture
        Assert.False(t.Feed(Press(10, 20), map, out _, out _, out _, out _));    // outside any region
        Assert.False(t.Feed(Release(10, 5), map, out _, out _, out _, out _));   // press missed: no capture
    }

    [Fact]
    public void ClickTracker_ChainCounts_SingleDoubleTriple_ThenResets()
    {
        var map = new MouseHitMap();
        map.Begin(80, 24);
        map.Add(new HitRegion(5, 1, 1, 80, MouseTargetKind.PickerItem, 3));
        map.Add(new HitRegion(7, 1, 1, 80, MouseTargetKind.PickerItem, 4));
        long tick = 1000;
        var t = new MouseClickTracker(() => tick);
        Assert.False(t.Feed(Press(10, 5), map, out _, out _, out _, out _));
        Assert.True(t.Feed(Release(10, 5), map, out _, out _, out _, out int d1));
        Assert.Equal(1, d1);
        tick += 200;   // inside the window
        Assert.False(t.Feed(Press(10, 5), map, out _, out _, out _, out _));
        Assert.True(t.Feed(Release(10, 5), map, out _, out _, out _, out int d2));
        Assert.Equal(2, d2);
        tick += 200;   // third fast click = TRIPLE (the F4/apply alias)
        Assert.False(t.Feed(Press(10, 5), map, out _, out _, out _, out _));
        Assert.True(t.Feed(Release(10, 5), map, out _, out _, out _, out int d3));
        Assert.Equal(3, d3);
        tick += 200;   // a triple RESETS the chain: a fourth fast click starts fresh
        Assert.False(t.Feed(Press(10, 5), map, out _, out _, out _, out _));
        Assert.True(t.Feed(Release(10, 5), map, out _, out _, out _, out int d4));
        Assert.Equal(1, d4);
    }

    [Fact]
    public void ClickTracker_DifferentTargetOrExpiredWindow_IsNotDouble()
    {
        var map = new MouseHitMap();
        map.Begin(80, 24);
        map.Add(new HitRegion(5, 1, 1, 80, MouseTargetKind.PickerItem, 3));
        map.Add(new HitRegion(7, 1, 1, 80, MouseTargetKind.PickerItem, 4));
        long tick = 1000;
        var t = new MouseClickTracker(() => tick);
        t.Feed(Press(10, 5), map, out _, out _, out _, out _);
        t.Feed(Release(10, 5), map, out _, out _, out _, out _);
        tick += 200;
        t.Feed(Press(10, 7), map, out _, out _, out _, out _);   // different payload
        Assert.True(t.Feed(Release(10, 7), map, out _, out _, out _, out int dOther));
        Assert.Equal(1, dOther);
        tick += MouseClickTracker.DoubleClickWindowMs + 1;        // expired
        t.Feed(Press(10, 7), map, out _, out _, out _, out _);
        Assert.True(t.Feed(Release(10, 7), map, out _, out _, out _, out int dLate));
        Assert.Equal(1, dLate);
    }

    // ---- view-level registration ----

    [Fact]
    public void AgentPickerView_Render_RegistersRosterRows_ClickItemMovesCursor()
    {
        var agents = Enumerable.Range(0, 5)
            .Select(i => AgentPickerViewTests.Agent($"Agent{i}", $"desc {i}")).ToList();
        var view = new AgentPickerView(agents, "Agent0");
        var hits = new MouseHitMap();
        var rows = view.Render(80, 24, hits);
        Assert.True(hits.Count >= agents.Count);
        // Region payloads resolve to roster indices; clicking one moves the cursor there.
        bool found = false;
        for (int r = 1; r <= rows.Count; r++)
            if (hits.TryHit(r, 2, out var hit, out _, out _) && hit is { Kind: MouseTargetKind.PickerItem, Payload: 3 })
                found = true;
        Assert.True(found, "roster row for index 3 must register a region");
        view.ClickItem(3);
        Assert.Equal("Agent3", view.Selected!.Name);
    }

    [Fact]
    public void SettingsPickerView_EditingMode_RegistersOnlyBackdrop()
    {
        var settings = new[]
        {
            new TuiConfigCommands.SettingSnapshot("alpha", Array.Empty<string>(), "a", "on|off", "on", false),
            new TuiConfigCommands.SettingSnapshot("beta", Array.Empty<string>(), "b", "on|off", "off", false),
        };
        var view = new SettingsPickerView(settings);
        var hits = new MouseHitMap();
        view.Render(60, 20, hits);
        Assert.True(hits.Count > 1);   // backdrop + item rows
        view.Handle(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false), 5);   // begin editing
        Assert.True(view.Editing);
        view.Render(60, 20, hits);
        // Editing: NO item rows - but the backdrop remains so clicks stay COUNTABLE (the
        // triple-click chain must survive the editor opening under click 2).
        Assert.Equal(1, hits.Count);
        Assert.True(hits.TryHit(5, 5, out var hit, out _, out _));
        Assert.Equal(MouseTargetKind.PickerSearchBox, hit.Kind);
    }

    [Fact]
    public void PromptModalView_Render_ReportsOptionRows_ForSelectKinds_NotText()
    {
        var view = new PromptModalView();
        view.Open(PromptModalView.Kind.Select, "pick one", new[] { "a", "b", "c" }, null, false);
        var optionRows = new List<(int Row, int Option)>();
        var rows = view.Render(80, 24, optionRows);
        Assert.Equal(3, optionRows.Count);
        Assert.All(optionRows, or => Assert.InRange(or.Row, 0, rows.Count - 1));
        Assert.Equal(new[] { 0, 1, 2 }, optionRows.Select(o => o.Option).ToArray());

        view.Open(PromptModalView.Kind.Text, "type", null, null, false);
        view.Render(80, 24, optionRows);
        Assert.Empty(optionRows);
    }

    [Fact]
    public void PromptModalView_SetSel_ClampsToChoices()
    {
        var view = new PromptModalView();
        view.Open(PromptModalView.Kind.Select, "q", new[] { "a", "b" }, null, false);
        view.SetSel(5);
        Assert.Equal(1, view.Sel);
        view.SetSel(-2);
        Assert.Equal(0, view.Sel);
    }

    // ---- modal loop integration (unstarted pump, no real console) ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AgentPicker_DoubleClickOnRosterRow_SelectsThatAgent(bool frame)
    {
        bool prompt = ConsoleInputPump.PromptActive, modal = ConsoleInputPump.ModalActive;
        ConsoleInputPump.PromptActive = false; ConsoleInputPump.ModalActive = false;
        var agents = Enumerable.Range(0, 4)
            .Select(i => AgentPickerViewTests.Agent($"Agent{i}", "d")).ToList();
        var view = new AgentPickerView(agents, "Agent0");
        var term = new Terminal();
        var driver = new TuiDriver(term, frameEngine: frame);
        driver.SetMouseTrackingPreset("buttons");
        using var pump = ConsoleInputPump.CreateUnstartedForTest();
        // Roster rows start after the 4 header rows: row 5 = index 0 ... row 7 = index 2.
        // GUI convention: double-click commits (two click pairs inside the window).
        pump.PushFront(new[] { Press(5, 7), Release(5, 7), Press(5, 7), Release(5, 7) });
        try
        {
            Assert.True(driver.RunAgentPicker(view, out var selected, input: pump));
            Assert.Equal("Agent2", selected!.Name);
        }
        finally { ConsoleInputPump.PromptActive = prompt; ConsoleInputPump.ModalActive = modal; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AgentPicker_SingleClick_SelectsOnly_DoesNotCommit(bool frame)
    {
        bool prompt = ConsoleInputPump.PromptActive, modal = ConsoleInputPump.ModalActive;
        ConsoleInputPump.PromptActive = false; ConsoleInputPump.ModalActive = false;
        var agents = Enumerable.Range(0, 4)
            .Select(i => AgentPickerViewTests.Agent($"Agent{i}", "d")).ToList();
        var view = new AgentPickerView(agents, "Agent0");
        var driver = new TuiDriver(new Terminal(), frameEngine: frame);
        driver.SetMouseTrackingPreset("buttons");
        using var pump = ConsoleInputPump.CreateUnstartedForTest();
        // Single click on row index 2, then Enter: the click must have moved the cursor there
        // (Enter selects Agent2), but the click ALONE must not have closed the picker.
        pump.PushFront(new[] { Press(5, 7), Release(5, 7), Key(ConsoleKey.Enter) });
        try
        {
            Assert.True(driver.RunAgentPicker(view, out var selected, input: pump));
            Assert.Equal("Agent2", selected!.Name);
        }
        finally { ConsoleInputPump.PromptActive = prompt; ConsoleInputPump.ModalActive = modal; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AgentPicker_ClickReleaseElsewhere_DoesNotSelect(bool frame)
    {
        bool prompt = ConsoleInputPump.PromptActive, modal = ConsoleInputPump.ModalActive;
        ConsoleInputPump.PromptActive = false; ConsoleInputPump.ModalActive = false;
        var agents = Enumerable.Range(0, 4)
            .Select(i => AgentPickerViewTests.Agent($"Agent{i}", "d")).ToList();
        var view = new AgentPickerView(agents, "Agent0");
        var driver = new TuiDriver(new Terminal(), frameEngine: frame);
        driver.SetMouseTrackingPreset("buttons");
        using var pump = ConsoleInputPump.CreateUnstartedForTest();
        pump.PushFront(new[] { Press(5, 7), Release(5, 9), Key(ConsoleKey.Escape) });
        try
        {
            Assert.True(driver.RunAgentPicker(view, out var selected, input: pump));
            Assert.Null(selected);   // drag-cancel: Esc ended the loop with nothing chosen
        }
        finally { ConsoleInputPump.PromptActive = prompt; ConsoleInputPump.ModalActive = modal; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModelPicker_DoubleClickOnSlotRow_AdvancesPane_LikeEnter(bool frame)
    {
        bool prompt = ConsoleInputPump.PromptActive, modal = ConsoleInputPump.ModalActive;
        ConsoleInputPump.PromptActive = false; ConsoleInputPump.ModalActive = false;
        var view = new ModelPickerView(new[] { new ModelSelectionStore.Slot("singleAgent", "Main", "old", "high") }, "provider");
        var driver = new TuiDriver(new Terminal(), frameEngine: frame);
        driver.SetMouseTrackingPreset("buttons");
        using var pump = ConsoleInputPump.CreateUnstartedForTest();
        // Slots pane: single slot row at row 7 (6 header rows). Double-click it (= Enter ->
        // Models pane), then Escape out. Neither click may Apply.
        pump.PushFront(new[] { Press(5, 7), Release(5, 7), Press(5, 7), Release(5, 7), Key(ConsoleKey.Escape) });
        try
        {
            Assert.True(driver.RunModelPicker(view,
                _ => Task.FromResult(new ProviderModelCatalog.Result(new[] { "m1" }, null)),
                (_, _, _) => Assert.Fail("clicks must never apply"), input: pump));
        }
        finally { ConsoleInputPump.PromptActive = prompt; ConsoleInputPump.ModalActive = modal; }
    }

    [Fact]
    public void InlineModal_ScopesMouseTracking_ToModalLifetime()
    {
        bool prompt = ConsoleInputPump.PromptActive, modal = ConsoleInputPump.ModalActive;
        ConsoleInputPump.PromptActive = false; ConsoleInputPump.ModalActive = false;
        var term = new Terminal();
        var driver = new TuiDriver(term, frameEngine: false);   // inline: tracking normally OFF
        driver.SetMouseTrackingPreset("buttons");
        Assert.DoesNotContain(Ansi.MouseTrackingButtonsOn, term.Output.ToString());
        var agents = new List<Common.AgentDefinition> { AgentPickerViewTests.Agent("A", "d") };
        using var pump = ConsoleInputPump.CreateUnstartedForTest();
        pump.PushFront(new[] { Key(ConsoleKey.Escape) });
        try
        {
            Assert.True(driver.RunAgentPicker(new AgentPickerView(agents, null), out _, input: pump));
            string output = term.Output.ToString();
            int on = output.IndexOf(Ansi.MouseTrackingButtonsOn, StringComparison.Ordinal);
            Assert.True(on >= 0, "modal entry must enable tracking under inline");
            // Tracking handed back off after the modal (the off string follows the enable).
            Assert.True(output.LastIndexOf(Ansi.MouseTrackingOff, StringComparison.Ordinal) > on,
                "modal exit must disable tracking again under inline");
        }
        finally { ConsoleInputPump.PromptActive = prompt; ConsoleInputPump.ModalActive = modal; }
    }

    [Fact]
    public void FramePromptModal_DoubleClickOnOption_SelectsIt_LikeEnter()
    {
        bool prompt = ConsoleInputPump.PromptActive, modal = ConsoleInputPump.ModalActive;
        ConsoleInputPump.PromptActive = false; ConsoleInputPump.ModalActive = false;
        var term = new Terminal();
        var driver = new TuiDriver(term, frameEngine: true);
        driver.SetMouseTrackingPreset("buttons");
        using var pump = ConsoleInputPump.CreateUnstartedForTest();
        var current = typeof(ConsoleInputPump).GetField("_current",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var prior = current.GetValue(null);
        current.SetValue(null, pump);
        try
        {
            // Compose once so the option regions exist, find option row 1, click it.
            var probe = Task.Run(() =>
            {
                // Wait for the modal to publish its regions (first Repaint inside RunPromptModal).
                for (int i = 0; i < 100; i++)
                {
                    var map = driver.FrameHitMap;
                    if (map is not null)
                        for (int r = 1; r <= 24; r++)
                            if (map.TryHit(r, 3, out var hit, out _, out _)
                                && hit is { Kind: MouseTargetKind.PromptModalOption, Payload: 1 })
                            {
                                pump.Enqueue(Press(3, r));
                                pump.Enqueue(Release(3, r));
                                pump.Enqueue(Press(3, r));
                                pump.Enqueue(Release(3, r));   // double-click = accept
                                return;
                            }
                    Thread.Sleep(20);
                }
                pump.Enqueue(Key(ConsoleKey.Escape));   // fail-safe: end the loop
            });
            var result = driver.RunPromptModal(PromptModalView.Kind.Select, "choose", new[] { "first", "second", "third" });
            probe.Wait(TimeSpan.FromSeconds(5));
            Assert.NotNull(result);
            Assert.False(result!.Cancelled);
            Assert.Equal(1, result.Index);
        }
        finally
        {
            current.SetValue(null, prior);
            ConsoleInputPump.PromptActive = prompt; ConsoleInputPump.ModalActive = modal;
        }
    }
}
