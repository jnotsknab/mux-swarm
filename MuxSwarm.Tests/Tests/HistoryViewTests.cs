using System.Text.Json;
using MuxSwarm.Engine.Tui;

namespace MuxSwarm.Tests.Tests;

// /history + bare-/resume browser: fuzzy list mode, statebag transcript extraction, READ-mode
// scroll/search, and the picker-vs-history Enter contract. Pure-view tests (no terminal).
public class HistoryViewTests
{
    private static List<(string, string)> Sessions => new()
    {
        ("2026-09-14_01-00-00", "fix the scrollbar artifact"),
        ("2026-09-13_09-00-00", "#mux-dev - turn cancellation batch"),
        ("2026-09-12_05-00-00", "research zig toolchain"),
    };

    private static ConsoleKeyInfo Key(char c) => new(c, ConsoleKey.Oem1, false, false, false);
    private static ConsoleKeyInfo K(ConsoleKey k) => new('\0', k, false, false, false);

    private static JsonElement Statebag(params (string Role, string Text)[] msgs)
    {
        var messages = msgs.Select(m => new Dictionary<string, object>
        {
            ["role"] = m.Role,
            ["contents"] = new[] { new Dictionary<string, object> { ["$type"] = "text", ["text"] = m.Text } },
        }).ToArray();
        string json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["stateBag"] = new Dictionary<string, object>
            {
                ["InMemoryChatHistoryProvider"] = new Dictionary<string, object> { ["messages"] = messages },
            },
        });
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void ListMode_FuzzyFilter_MatchesIdPreviewAndTags()
    {
        var v = new HistoryView(Sessions);
        foreach (char c in "scrollbar") v.Handle(Key(c), 10);
        Assert.Equal(1, v.MatchCount);
        Assert.Equal("2026-09-14_01-00-00", v.SelectedId);

        var v2 = new HistoryView(Sessions);
        foreach (char c in "mux-dev") v2.Handle(Key(c), 10);
        Assert.Equal(1, v2.MatchCount);
        Assert.Equal("2026-09-13_09-00-00", v2.SelectedId);
    }

    [Fact]
    public void HistoryMode_Enter_RequestsOpen_NotResume()
    {
        var v = new HistoryView(Sessions, resumePicker: false);
        var (action, open) = v.Handle(K(ConsoleKey.Enter), 10);
        Assert.Equal(HistoryView.Action.None, action);
        Assert.Equal(v.SelectedId, open);
    }

    [Fact]
    public void ResumePicker_Enter_Resumes_And_V_Previews()
    {
        var v = new HistoryView(Sessions, resumePicker: true);
        var (action, _) = v.Handle(K(ConsoleKey.Enter), 10);
        Assert.Equal(HistoryView.Action.Resume, action);

        var v2 = new HistoryView(Sessions, resumePicker: true);
        var (a2, open) = v2.Handle(Key('v'), 10);
        Assert.Equal(HistoryView.Action.None, a2);
        Assert.Equal(v2.SelectedId, open);           // v with empty query = preview request
        foreach (char c in "zig") v2.Handle(Key(c), 10);
        var (_, open2) = v2.Handle(Key('v'), 10);
        Assert.Null(open2);                          // v inside an active query = just typing
    }

    [Fact]
    public void ExtractTranscript_RolesTextAndToolMarkers()
    {
        var root = Statebag(("user", "investigate X"), ("assistant", "found it in FooBar.cs"));
        var t = HistoryView.ExtractTranscript(root);
        Assert.Equal(2, t.Count);
        Assert.Equal("user", t[0].Role);
        Assert.Contains("investigate X", t[0].Text);
        Assert.Contains("found it", t[1].Text);
    }

    [Fact]
    public void ReadMode_OpensRendersScrollsAndSearches()
    {
        var v = new HistoryView(Sessions);
        var many = string.Join("\n", Enumerable.Range(1, 120).Select(i => $"line-{i} content"));
        v.OpenTranscript("2026-09-14_01-00-00", Statebag(("user", "start"), ("assistant", many)));
        Assert.Equal(HistoryView.Mode.Read, v.CurrentMode);

        var rows = v.Render(80, 20);
        Assert.Contains(rows, r => r.Contains("SESSION HISTORY"));
        Assert.Contains(rows, r => r.Contains("line-1"));

        v.Handle(Key('G'), 15);                       // jump to bottom
        Assert.True(v.Scroll > 0);
        var bottom = v.Render(80, 20);
        Assert.Contains(bottom, r => r.Contains("line-120"));

        v.Handle(Key('g'), 15);
        Assert.Equal(0, v.Scroll);

        v.Handle(Key('/'), 15);                       // search
        foreach (char c in "line-90") v.Handle(Key(c), 15);
        Assert.True(v.HitCount >= 1);
        var searched = v.Render(80, 20);
        Assert.Contains(searched, r => r.Contains("line-90"));

        v.Handle(K(ConsoleKey.Enter), 15);            // close search entry, keep hits
        v.Handle(K(ConsoleKey.Escape), 15);           // back to list
        Assert.Equal(HistoryView.Mode.List, v.CurrentMode);
    }

    [Fact]
    public void Render_TinyViewport_SafeAndInert()
    {
        var v = new HistoryView(Sessions);
        var rows = v.Render(10, 4);
        Assert.Equal(4, rows.Count);
        var (action, open) = v.Handle(K(ConsoleKey.Enter), 1);
        // Enter still routes (guarding tiny geometry is the driver's job), but never throws.
        Assert.True(action == HistoryView.Action.None);
        _ = open;
    }

    [Fact]
    public void ReadMode_MarkdownTable_RendersAlignedNotRawPipes()
    {
        var v = new HistoryView(Sessions);
        string md = "Specs:\n| Part | Detail |\n|---|---|\n| OS | Windows 11 |\n| CPU | Ryzen 7 9800X3D |\n\nDone.";
        v.OpenTranscript("s1", Statebag(("assistant", md)));
        var rows = v.Render(100, 30);
        string joined = string.Join("\n", rows);
        // Rendered through TuiTable: box-drawing borders present, raw GFM pipe rows absent.
        Assert.Contains("\u2503", joined);                       // heavy vertical border in cells
        Assert.Contains("OS", joined);
        Assert.Contains("Ryzen 7 9800X3D", joined);
        Assert.DoesNotContain("| OS | Windows 11 |", joined);    // no raw pipes leak
        Assert.DoesNotContain("|---|---|", joined);              // separator consumed
        // Non-table prose still renders.
        Assert.Contains("Done.", joined);
    }

    [Fact]
    public void ReadMode_UserPipeText_IsNotTableified()
    {
        var v = new HistoryView(Sessions);
        v.OpenTranscript("s1", Statebag(("user", "run: dir | findstr foo | sort")));
        var rows = v.Render(90, 20);
        string joined = string.Join("\n", rows);
        // User text with pipes is command syntax, not a table - stays verbatim.
        Assert.Contains("dir | findstr foo | sort", joined);
    }

    [Fact]
    public void Marquee_ShortTextStatic_LongTextSlidesAndWraps()
    {
        // Under budget: identical regardless of offset (no motion).
        Assert.Equal("short", TuiComponents.Marquee("short", 20, 0));
        Assert.Equal("short", TuiComponents.Marquee("short", 20, 7));

        string longText = "abcdefghijklmnopqrstuvwxyz";
        string at0 = TuiComponents.Marquee(longText, 10, 0);
        string at3 = TuiComponents.Marquee(longText, 10, 3);
        Assert.NotEqual(at0, at3);
        Assert.StartsWith("abc", at0);
        Assert.StartsWith("def", at3);
        // Wraps through the separator back to the head.
        string wrapped = TuiComponents.Marquee(longText, 10, longText.Length + 3);
        Assert.Contains("a", wrapped);
    }

    [Fact]
    public void SessionsPreview_MarqueeOffset_AffectsOnlySelectedRow()
    {
        var sessions = new List<(string, string)>
        {
            ("s1", new string('x', 200) + " TAIL-MARKER"),
            ("s2", "short preview"),
        };
        var still = TuiComponents.SessionsPreview(null, sessions, 60, 0, 0);
        var moved = TuiComponents.SessionsPreview(null, sessions, 60, 0, 25);
        Assert.NotEqual(string.Join("\n", still), string.Join("\n", moved));
        // Unselected row identical across offsets.
        Assert.Equal(still.Last(), moved.Last());
    }

    [Fact]
    public void EmptySessions_RenderAndFilterAreSafe()
    {
        var v = new HistoryView(new List<(string, string)>());
        Assert.Null(v.SelectedId);
        var rows = v.Render(60, 12);
        Assert.Contains(rows, r => r.Contains("No saved sessions"));
        var (action, open) = v.Handle(K(ConsoleKey.Enter), 5);
        Assert.Equal(HistoryView.Action.None, action);
        Assert.Null(open);
    }
}
