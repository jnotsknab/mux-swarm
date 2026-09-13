using MuxSwarm.Engine;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

/// <summary>Full-screen ownership with an unstarted pump; no live terminal input is consumed.</summary>
[Collection("ConsoleState")]
public class ModelPickerDriverTests
{
    private sealed class Terminal : ITuiTerminal
    {
        public int Width { get; set; } = 90;
        public int Height { get; set; } = 22;
        public System.Text.StringBuilder Output { get; } = new();
        public void Write(string text) => Output.Append(text);
        public void Flush() { }
    }
    private static ModelPickerView View() => new(new[] { new ModelSelectionStore.Slot("singleAgent", "Main", "old", "high") }, "provider");
    private static ConsoleInputPump.InputEvent Key(ConsoleKey key)
        => ConsoleInputPump.InputEvent.OfKey(new ConsoleKeyInfo('\0', key, false, false, false));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancel_RestoresRendererAndPumpOwnershipWithoutSaving(bool frame)
    {
        bool prompt = ConsoleInputPump.PromptActive, modal = ConsoleInputPump.ModalActive;
        ConsoleInputPump.PromptActive = false; ConsoleInputPump.ModalActive = false;
        var term = new Terminal();
        var driver = new TuiDriver(term, frameEngine: frame);
        driver.CommitLine("original transcript");
        using var pump = ConsoleInputPump.CreateUnstartedForTest();
        pump.PushFront(new[] { Key(ConsoleKey.Escape) });
        int writes = 0;
        try
        {
            Assert.True(driver.RunModelPicker(View(), _ => Task.FromResult(new ProviderModelCatalog.Result(new[] { "new" }, null)),
                (_, _, _) => writes++, input: pump));
            Assert.Equal(0, writes);
            Assert.False(ConsoleInputPump.PromptActive);
            Assert.False(ConsoleInputPump.ModalActive);
            Assert.Contains("MODEL SETTINGS", term.Output.ToString());
            if (!frame) Assert.Contains(Ansi.LeaveAltScreen, term.Output.ToString());
            Assert.Contains(driver.ComposeFrameRows(), row => row.Contains("original transcript"));
        }
        finally { ConsoleInputPump.PromptActive = prompt; ConsoleInputPump.ModalActive = modal; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_InvokesOneSaveWithSelectedModelAndEffort(bool frame)
    {
        bool prompt = ConsoleInputPump.PromptActive, modal = ConsoleInputPump.ModalActive;
        ConsoleInputPump.PromptActive = false; ConsoleInputPump.ModalActive = false;
        using var pump = ConsoleInputPump.CreateUnstartedForTest();
        pump.PushFront(new[] { Key(ConsoleKey.Enter), Key(ConsoleKey.UpArrow), Key(ConsoleKey.Enter), Key(ConsoleKey.RightArrow), Key(ConsoleKey.F4) });
        var driver = new TuiDriver(new Terminal(), frameEngine: frame);
        int writes = 0;
        try
        {
            driver.RunModelPicker(View(), _ => Task.FromResult(new ProviderModelCatalog.Result(new[] { "new", "old" }, null)),
                (slot, model, effort) => { writes++; Assert.Equal("singleAgent", slot); Assert.Equal("new", model); Assert.Equal("xhigh", effort); }, input: pump);
            Assert.Equal(1, writes);
        }
        finally { ConsoleInputPump.PromptActive = prompt; ConsoleInputPump.ModalActive = modal; }
    }

    [Fact]
    public void CancelDuringDiscovery_CancelsPendingRequestPromptly()
    {
        bool prompt = ConsoleInputPump.PromptActive, modal = ConsoleInputPump.ModalActive;
        ConsoleInputPump.PromptActive = false; ConsoleInputPump.ModalActive = false;
        using var pump = ConsoleInputPump.CreateUnstartedForTest();
        pump.PushFront(new[] { Key(ConsoleKey.Escape) });
        var pending = new TaskCompletionSource<ProviderModelCatalog.Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken requested = default;
        try
        {
            new TuiDriver(new Terminal(), frameEngine: true).RunModelPicker(View(), ct => { requested = ct; return pending.Task; },
                (_, _, _) => Assert.Fail("Cancel must not apply"), input: pump);
            Assert.True(requested.IsCancellationRequested);
        }
        finally { pending.TrySetCanceled(); ConsoleInputPump.PromptActive = prompt; ConsoleInputPump.ModalActive = modal; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaveFailure_StaysOpenThenCancelRestoresOwnership(bool frame)
    {
        bool prompt = ConsoleInputPump.PromptActive, modal = ConsoleInputPump.ModalActive;
        ConsoleInputPump.PromptActive = false; ConsoleInputPump.ModalActive = false;
        using var pump = ConsoleInputPump.CreateUnstartedForTest();
        pump.PushFront(new[] { Key(ConsoleKey.F4), Key(ConsoleKey.Escape) });
        var view = View();
        int attempted = 0;
        try
        {
            new TuiDriver(new Terminal(), frameEngine: frame).RunModelPicker(view,
                _ => Task.FromResult(new ProviderModelCatalog.Result([], "offline")),
                (_, _, _) => { attempted++; throw new IOException("private path or concurrent edit"); }, input: pump);
            Assert.Equal(1, attempted);
            Assert.Contains("Close and reopen", view.Message);
            Assert.False(ConsoleInputPump.PromptActive);
            Assert.False(ConsoleInputPump.ModalActive);
        }
        finally { ConsoleInputPump.PromptActive = prompt; ConsoleInputPump.ModalActive = modal; }
    }

    [Fact]
    public void Refresh_CancelsOldRequestAndLateResultCannotReplaceNewCatalog()
    {
        bool prompt = ConsoleInputPump.PromptActive, modal = ConsoleInputPump.ModalActive;
        ConsoleInputPump.PromptActive = false; ConsoleInputPump.ModalActive = false;
        using var pump = ConsoleInputPump.CreateUnstartedForTest();
        pump.PushFront(new[] { Key(ConsoleKey.F5), Key(ConsoleKey.Escape) });
        var pending = new TaskCompletionSource<ProviderModelCatalog.Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken first = default;
        int calls = 0;
        var view = View();
        try
        {
            new TuiDriver(new Terminal(), frameEngine: true).RunModelPicker(view, ct =>
            {
                if (++calls == 1) { first = ct; return pending.Task; }
                pending.TrySetResult(new ProviderModelCatalog.Result(new[] { "stale" }, null));
                return Task.FromResult(new ProviderModelCatalog.Result(new[] { "fresh-a", "fresh-b" }, null));
            }, (_, _, _) => Assert.Fail("refresh is not Apply"), input: pump);
            Assert.True(first.IsCancellationRequested);
            Assert.Equal(2, calls);
            Assert.StartsWith("2 models", view.Message);
        }
        finally { pending.TrySetCanceled(); ConsoleInputPump.PromptActive = prompt; ConsoleInputPump.ModalActive = modal; }
    }
}
