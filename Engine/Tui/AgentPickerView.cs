namespace MuxSwarm.Engine.Tui;

/// <summary>Searchable agent roster with staged navigation; only Enter returns selection intent.</summary>
internal sealed class AgentPickerView
{
    /// <summary>Signals to the modal owner; browsing never changes an agent definition.</summary>
    internal enum Action { None, Cancel, Select }
    internal const int MinWidth = 20;
    internal const int MinHeight = 10;
    private const int MaxQueryLength = 256;
    private readonly Common.AgentDefinition[] _agents;
    private List<int> _matches;
    private int _index;
    private string _query = "";

    /// <summary>Snapshot roster order while retaining the exact definitions used by the existing swap handler.</summary>
    internal AgentPickerView(IReadOnlyList<Common.AgentDefinition> agents, string? currentName)
    {
        _agents = agents.ToArray();
        CurrentName = currentName;
        _matches = Enumerable.Range(0, _agents.Length).ToList();
        _index = Math.Max(0, Array.FindIndex(_agents, a => a.Name.Equals(currentName, StringComparison.Ordinal)));
    }

    /// <summary>The active agent name at opening, not a staged change.</summary>
    internal string? CurrentName { get; }
    /// <summary>Current inert search text.</summary>
    internal string Query => _query;
    /// <summary>Definition under the cursor, or null when the filter has no match.</summary>
    internal Common.AgentDefinition? Selected => _matches.Count == 0 ? null : _agents[_matches[_index]];
    /// <summary>Current match count; description terms and fuzzy name terms must all match.</summary>
    internal int MatchCount => _matches.Count;

    private static string Display(string text)
        => string.Join(" ", new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray())
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static int FuzzyName(string name, string term)
    {
        int hit = name.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (hit >= 0) return hit;
        int next = 0, first = -1, last = -1;
        for (int i = 0; i < name.Length && next < term.Length; i++)
            if (char.ToUpperInvariant(name[i]) == char.ToUpperInvariant(term[next]))
            { if (first < 0) first = i; last = i; next++; }
        return next == term.Length ? 1000 + last - first : -1;
    }

    private static int Score(Common.AgentDefinition agent, string query, string[] terms)
    {
        if (query.Length == 0 || agent.Name.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0;
        int total = 1;
        foreach (var term in terms)
        {
            int name = FuzzyName(agent.Name, term);
            int description = agent.Description?.IndexOf(term, StringComparison.OrdinalIgnoreCase) ?? -1;
            int score = Math.Min(name < 0 ? int.MaxValue : name,
                description < 0 ? int.MaxValue : 2000 + description);
            if (score == int.MaxValue) return -1;
            total += score;
        }
        return total;
    }

    private void Filter()
    {
        string query = _query.Trim();
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        _matches = Enumerable.Range(0, _agents.Length)
            .Select(i => (Index: i, Score: Score(_agents[i], query, terms)))
            .Where(x => x.Score >= 0).OrderBy(x => x.Score).ThenBy(x => x.Index)
            .Select(x => x.Index).ToList();
        _index = 0;
    }

    /// <summary>Append bounded, control-free search text; paste can never select an agent.</summary>
    internal void Paste(string text)
    {
        string value = new(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        int remaining = Math.Max(0, MaxQueryLength - _query.Length);
        if (value.Length > remaining)
        {
            int end = remaining;
            if (end > 0 && char.IsHighSurrogate(value[end - 1])) end--;
            value = value[..end];
        }
        _query += value;
        Filter();
    }

    /// <summary>Navigate/filter the snapshot, or return explicit select/cancel intent.</summary>
    internal Action Handle(ConsoleKeyInfo key, int pageSize)
    {
        bool ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        if (key.Key == ConsoleKey.Escape || (ctrl && key.Key is ConsoleKey.Q or ConsoleKey.C)) return Action.Cancel;
        if (ctrl && key.Key == ConsoleKey.U) { _query = ""; Filter(); return Action.None; }
        if (ctrl || key.Modifiers.HasFlag(ConsoleModifiers.Alt)) return Action.None;
        if (key.Key == ConsoleKey.Enter) return Selected is null ? Action.None : Action.Select;
        int delta = key.Key switch
        {
            ConsoleKey.UpArrow => -1,
            ConsoleKey.DownArrow => 1,
            ConsoleKey.PageUp => -Math.Max(1, pageSize),
            ConsoleKey.PageDown => Math.Max(1, pageSize),
            ConsoleKey.Home => -_matches.Count,
            ConsoleKey.End => _matches.Count,
            _ => 0
        };
        if (delta != 0) _index = (int)Math.Clamp((long)_index + delta, 0, Math.Max(0, _matches.Count - 1));
        else if (key.Key == ConsoleKey.Backspace && _query.Length > 0)
        {
            var starts = System.Globalization.StringInfo.ParseCombiningCharacters(_query);
            _query = _query[..starts[^1]];
            Filter();
        }
        else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar)) Paste(key.KeyChar.ToString());
        return Action.None;
    }

    /// <summary>Move the cursor to a clicked list item (region payload from the last render). The
    /// caller dispatches Enter so selection intent stays keyboard-identical.</summary>
    internal void ClickItem(int index)
        => _index = Math.Clamp(index, 0, Math.Max(0, _matches.Count - 1));

    /// <summary>Bounded rows with a visible selection at any supported size; controls remain inert markup.
    /// When <paramref name="hits"/> is supplied, roster rows register PickerItem regions
    /// (payload = match index) from the same emission that painted them.</summary>
    internal List<string> Render(int width, int height, MouseHitMap? hits = null)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        hits?.Begin(width, height);
        string Esc(string s) => Spectre.Console.Markup.Escape(Display(s));
        string Clip(string s) => TuiMarkup.TruncateMarkup(s, width, "");
        if (width < MinWidth || height < MinHeight)
            return new[] { "Esc: cancel", $"Resize to {MinWidth} columns / {MinHeight} rows to choose a lead agent." }
                .Concat(Enumerable.Repeat("", height)).Take(height).Select(Clip).ToList();
        var rows = new List<string>
        {
            $"[{TuiComponents.Accent} bold] CHOOSE LEAD AGENT[/]",
            $"[{TuiComponents.Muted}] Current lead: {Esc(CurrentName ?? "not selected")}[/]",
            $"[{TuiComponents.Accent}] Search: {Esc(_query)}▏[/]",
            TuiComponents.FullRule(width)
        };
        int room = height - 8;
        int start = Math.Clamp(_index - room / 2, 0, Math.Max(0, _matches.Count - room));
        if (_matches.Count == 0)
            rows.Add($"[{TuiComponents.Muted}] {(_agents.Length == 0 ? "No agents configured." : "No matches. Backspace or Ctrl+U clears the filter.")}[/]");
        for (int i = start; i < Math.Min(_matches.Count, start + room); i++)
        {
            var agent = _agents[_matches[i]];
            string active = agent.Name.Equals(CurrentName, StringComparison.Ordinal) ? " (current)" : "";
            hits?.Add(new HitRegion(rows.Count + 1, 1, 1, width, MouseTargetKind.PickerItem, i));
            rows.Add($"[{(i == _index ? TuiComponents.Accent : TuiComponents.Muted)}]{(i == _index ? "›" : " ")} {Esc(agent.Name)}{active}[/]");
        }
        while (rows.Count < height - 4) rows.Add("");
        rows.Add($"[{TuiComponents.Text}] {Esc(Selected?.Description ?? "Select an agent to see its description.")}[/]");
        rows.Add($"[{TuiComponents.Muted}] {_matches.Count}/{_agents.Length} agents · Next /agent run; no config write[/]");
        rows.Add($"[{TuiComponents.Accent}] Enter choose · ↑↓/PgUp/PgDn/Home/End navigate[/]");
        rows.Add($"[{TuiComponents.Muted}] Type to search · Ctrl+U clear · Esc/Ctrl+Q cancel[/]");
        return rows.Select(Clip).ToList();
    }
}
