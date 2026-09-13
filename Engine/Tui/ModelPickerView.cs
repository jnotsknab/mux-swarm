namespace MuxSwarm.Engine.Tui;

/// <summary>Pure keyboard-driven model/effort picker. Navigation never changes persisted settings.</summary>
internal sealed class ModelPickerView
{
    /// <summary>Commands returned to the driver; Apply is the only write intent.</summary>
    internal enum Action { None, Cancel, Refresh, Apply }
    private enum Pane { Slots, Models, Effort }
    private static readonly string?[] Efforts = [null, "none", "low", "med", "high", "xhigh", "max"];
    private readonly IReadOnlyList<ModelSelectionStore.Slot> _slots;
    private IReadOnlyList<string> _models = [];
    private Pane _pane;
    private int _slotIndex, _modelIndex;
    private string _query = "";
    private bool _manual;
    private string _custom = "";
    private string _model = "";
    private string? _effort;
    private bool _customEffort;

    /// <summary>Create staged settings from a snapshot; no IO or writes.</summary>
    internal ModelPickerView(IReadOnlyList<ModelSelectionStore.Slot> slots, string provider)
    {
        _slots = slots;
        Provider = provider;
        SelectSlot();
    }

    /// <summary>Display name of the snapshotted active provider.</summary>
    internal string Provider { get; }
    /// <summary>True while a catalog request is pending.</summary>
    internal bool Loading { get; set; } = true;
    /// <summary>Safe discovery or save feedback displayed in the footer.</summary>
    internal string? Message { get; set; }
    /// <summary>Selected persisted slot, identified independently of duplicate labels.</summary>
    internal ModelSelectionStore.Slot? Slot => _slots.Count > 0 ? _slots[_slotIndex] : null;
    /// <summary>Model staged for explicit Apply.</summary>
    internal string Model => _manual ? _query.Trim() : _model;
    /// <summary>Canonical preset/custom value, or null to remove the override.</summary>
    internal string? Effort => _customEffort ? "custom " + _custom.Trim() : _effort;

    /// <summary>Replace a completed catalog without applying a model or effort.</summary>
    internal void SetModels(IReadOnlyList<string> models, string? error)
    {
        _models = models;
        Loading = false;
        Message = error ?? $"{models.Count} models advertised. Availability and effort support are provider-specific.";
        var matches = Matches();
        _modelIndex = Math.Max(0, matches.FindIndex(m => m == _model));
    }

    private List<string> Matches() => _models.Where(m => m.Contains(_query, StringComparison.OrdinalIgnoreCase)).ToList();

    private void SelectSlot()
    {
        _model = Slot?.Model ?? "";
        _effort = ReasoningEffortControl.TryParse(Slot?.Effort, out var stored) ? stored!.Label : Slot?.Effort;
        _customEffort = _effort?.StartsWith("custom ", StringComparison.OrdinalIgnoreCase) == true;
        _custom = _customEffort ? _effort![7..] : "";
        _query = "";
        _manual = false;
        _modelIndex = Math.Max(0, Matches().FindIndex(m => m == _model));
    }

    /// <summary>Append filtered text to the focused editor; never treats paste as Apply.</summary>
    internal void Paste(string text)
    {
        // Filter terminal controls; paste never implies selection or Apply.
        foreach (char c in text.Where(c => !char.IsControl(c))) Append(c);
    }

    private void Append(char c)
    {
        if (_pane == Pane.Models && _query.Length < 1024) { _query += c; _modelIndex = 0; }
        else if (_pane == Pane.Effort && _customEffort && _custom.Length < 128) _custom += c;
    }

    /// <summary>Stage navigation/editing or return an explicit lifecycle command.</summary>
    internal Action Handle(ConsoleKeyInfo key, int pageSize)
    {
        bool ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        if (key.Key == ConsoleKey.Escape || (ctrl && key.Key == ConsoleKey.Q)) return Action.Cancel;
        if (key.Key == ConsoleKey.F5) return Action.Refresh;
        if (key.Key == ConsoleKey.F4)
        {
            if (_pane == Pane.Models && !ChooseModel()) return Action.None;
            if (Slot is null || Model.Length == 0) { Message = "Select a slot and model before applying."; return Action.None; }
            if (Effort is not null && !ReasoningEffortControl.TryParse(Effort, out _))
                { Message = "Enter a custom effort value, or select a preset."; return Action.None; }
            return Action.Apply;
        }
        if (key.Key == ConsoleKey.Tab)
        {
            if (_pane == Pane.Models) ChooseModel();
            int step = key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? 2 : 1;
            _pane = (Pane)(((int)_pane + step) % 3);
            return Action.None;
        }
        if (_pane == Pane.Models && key.Key == ConsoleKey.F2)
        {
            _manual = !_manual; _query = _manual ? _model : ""; _modelIndex = 0;
            return Action.None;
        }
        if (_pane == Pane.Effort && key.Key == ConsoleKey.F2)
            { _customEffort = !_customEffort; return Action.None; }
        int delta = key.Key switch
        {
            ConsoleKey.UpArrow or ConsoleKey.LeftArrow => -1,
            ConsoleKey.DownArrow or ConsoleKey.RightArrow => 1,
            ConsoleKey.PageUp => -Math.Max(1, pageSize),
            ConsoleKey.PageDown => Math.Max(1, pageSize),
            ConsoleKey.Home => -20000,
            ConsoleKey.End => 20000,
            _ => 0
        };
        if (_pane == Pane.Slots && delta != 0 && _slots.Count > 0)
        {
            _slotIndex = Math.Clamp(_slotIndex + delta, 0, _slots.Count - 1); SelectSlot();
        }
        else if (_pane == Pane.Models && !_manual && delta != 0)
            _modelIndex = Math.Clamp(_modelIndex + delta, 0, Math.Max(0, Matches().Count - 1));
        else if (_pane == Pane.Effort && delta != 0)
        {
            _customEffort = false;
            int index = Array.IndexOf(Efforts, _effort);
            _effort = Efforts[(Math.Max(0, index) + (delta > 0 ? 1 : Efforts.Length - 1)) % Efforts.Length];
        }
        else if (key.Key == ConsoleKey.Enter)
        {
            if (_pane == Pane.Slots) _pane = Pane.Models;
            else if (_pane == Pane.Models)
            {
                if (ChooseModel()) _pane = Pane.Effort;
            }
        }
        else if (key.Key == ConsoleKey.Backspace)
        {
            if (_pane == Pane.Models && _query.Length > 0) { _query = DropLast(_query); _modelIndex = 0; }
            if (_pane == Pane.Effort && _customEffort && _custom.Length > 0) _custom = DropLast(_custom);
        }
        else if (!ctrl && key.KeyChar != '\0' && !char.IsControl(key.KeyChar)) Append(key.KeyChar);
        return Action.None;
    }

    private bool ChooseModel()
    {
        if (_manual) { _model = _query.Trim(); return _model.Length > 0; }
        var matches = Matches();
        if (matches.Count == 0)
        {
            Message = "No matching model selected. F2 enters a model manually.";
            return false;
        }
        _model = matches[Math.Min(_modelIndex, matches.Count - 1)];
        return true;
    }

    private static string DropLast(string text)
    {
        int[] starts = System.Globalization.StringInfo.ParseCombiningCharacters(text);
        return starts.Length == 0 ? "" : text[..starts[^1]];
    }

    /// <summary>Exactly height bounded markup rows, with a single active pane at narrow widths.</summary>
    internal List<string> Render(int width, int height)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        string Esc(string s) => Spectre.Console.Markup.Escape(s);
        string Clip(string s) => TuiMarkup.TruncateMarkup(s, width, "");
        var rows = new List<string>
        {
            $"[{TuiComponents.Accent} bold] MODEL SETTINGS[/] [{TuiComponents.Muted}]— {Esc(Provider)}[/]",
            $"[{TuiComponents.Muted}] Tab: Slots / Models / Effort · Active: {_pane}[/]",
            $"[{TuiComponents.Text}] {Esc((Slot?.Label ?? "No slots") + " (" + (Slot?.Id ?? "") + ")")}[/]",
            $"[{TuiComponents.Muted}] Model: {Esc(Model.Length > 0 ? Model : "(select)")}[/]",
            $"[{TuiComponents.Muted}] Effort: {Esc(Effort ?? "default (no override)")} · saved on F4 Apply; next run[/]",
            TuiComponents.FullRule(width),
        };
        if (height < 12)
        {
            var small = new[]
            {
                $"[{TuiComponents.Accent}] Model settings · {_pane}[/]",
                $"[{TuiComponents.Muted}] Resize to at least 12 rows to browse safely.[/]",
                $"[{TuiComponents.Muted}] Esc/Ctrl+Q cancel[/]"
            };
            return small.Concat(Enumerable.Repeat("", height)).Take(height).Select(Clip).ToList();
        }
        int room = Math.Max(2, height - 10);
        if (_pane == Pane.Slots)
        {
            int start = Math.Clamp(_slotIndex - room / 2, 0, Math.Max(0, _slots.Count - room));
            for (int i = start; i < Math.Min(_slots.Count, start + room); i++)
                rows.Add($"[{(i == _slotIndex ? TuiComponents.Accent : TuiComponents.Muted)}]{(i == _slotIndex ? "›" : " ")} {Esc(_slots[i].Label)} · {Esc(_slots[i].Model ?? "unset")}[/]");
        }
        else if (_pane == Pane.Models)
        {
            rows.Add($"[{TuiComponents.Accent}] {(_manual ? "Manual model" : "Search")}: {Esc(_query)}▏[/]");
            var matches = Matches();
            int modelRoom = Math.Max(1, room - 1);
            int start = Math.Clamp(_modelIndex - modelRoom / 2, 0, Math.Max(0, matches.Count - modelRoom));
            if (!_manual)
                for (int i = start; i < Math.Min(matches.Count, start + modelRoom); i++)
                    rows.Add($"[{(i == _modelIndex ? TuiComponents.Accent : TuiComponents.Muted)}]{(i == _modelIndex ? "›" : " ")} {Esc(matches[i])}{(matches[i] == Slot?.Model ? " (current)" : "")}[/]");
            if (!_manual && matches.Count == 0) rows.Add($"[{TuiComponents.Muted}] {(Loading ? "Loading models…" : "No matches. F2: enter model manually.")}[/]");
        }
        else
        {
            rows.Add($"[{TuiComponents.Accent}] ←/→ effort: {Esc(Effort ?? "default (no override)")}[/]");
            rows.Add($"[{TuiComponents.Muted}] default · none · low · med · high · xhigh · max[/]");
            rows.Add($"[{TuiComponents.Muted}] F2: {(_customEffort ? "presets" : "custom effort")}{(_customEffort ? " · type value: " + Esc(_custom) + "▏" : "")}[/]");
            rows.Add($"[{TuiComponents.Muted}] /models does not guarantee effort support; provider errors stay visible.[/]");
        }
        int bodyEnd = Math.Max(0, height - 3);
        if (rows.Count > bodyEnd) rows = rows.Take(bodyEnd).ToList();
        while (rows.Count < bodyEnd) rows.Add("");
        rows.Add($"[{TuiComponents.Muted}] {Esc(Loading ? "Querying active provider…" : Message ?? "")}[/]");
        rows.Add($"[{TuiComponents.Accent}] F4 Apply · Enter choose · Tab pane · ↑↓/PgUp/PgDn navigate[/]");
        rows.Add($"[{TuiComponents.Muted}] F2 manual/custom · F5 refresh · Esc/Ctrl+Q cancel[/]");
        return rows.TakeLast(height).Select(Clip).ToList();
    }
}
