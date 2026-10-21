using System.Text.Json;

namespace MuxSwarm.Engine.Extensions;

/// <summary>
/// Panels extensions open with <c>ui/open</c>: a declarative element tree the process pushes and
/// Mux draws (alt-screen view in the TUI, side panel on the web). Mux never calls the process to draw;
/// it renders the cached tree and sends user actions back as <c>ui/event</c> notifications.
/// Surface-agnostic: renderers subscribe to <see cref="Changed"/> / <see cref="Closed"/>.
/// </summary>
internal static class ExtensionPanels
{
    /// <summary>Element types a tree may use.</summary>
    internal static readonly string[] Types = ["box", "text", "button", "input", "list", "markdown", "code", "progress"];

    /// <summary>Theme roles a <c>color</c> may name.</summary>
    internal static readonly string[] Colors = ["text", "muted", "accent", "success", "warning", "error", "info"];

    internal const int MaxDepth = 32, MaxNodes = 2000, MaxTextChars = 100_000, MaxPanelsPerExtension = 8;

    // Props each type accepts (besides "type"). Anything else is an error naming it.
    private static readonly Dictionary<string, string[]> Props = new()
    {
        ["box"] = ["dir", "gap", "border", "children"],
        ["text"] = ["text", "color", "bold", "dim"],
        ["button"] = ["key", "label", "hotkey"],
        ["input"] = ["key", "placeholder", "value"],
        ["list"] = ["key", "items", "selected"],
        ["markdown"] = ["text"],
        ["code"] = ["text"],
        ["progress"] = ["value", "label"],
    };

    /// <summary>One validated element. Unused fields stay at their defaults.</summary>
    internal sealed record Node(string Type, string? Key = null, string Text = "", string? Color = null, bool Bold = false,
        bool Dim = false, string Dir = "col", int Gap = 0, string Border = "none", char? Hotkey = null, string? Placeholder = null,
        double Value = 0, string? Selected = null, IReadOnlyList<Item>? Items = null, IReadOnlyList<Node>? Children = null)
    {
        internal IReadOnlyList<Item> ItemList => Items ?? [];
        internal IReadOnlyList<Node> Kids => Children ?? [];
        /// <summary>Buttons, inputs and lists take keyboard focus.</summary>
        internal bool Focusable => Type is "button" or "input" or "list";
    }

    /// <summary>One <c>list</c> row: <c>value</c> is sent back on select.</summary>
    internal sealed record Item(string Value, string Label, string? Detail);

    /// <summary>An open panel. <paramref name="View"/> is the tree as the process sent it (for the web).</summary>
    internal sealed record Panel(string Extension, string Id, string Title, Node Root, JsonElement View, int Version);

    private static readonly object Gate = new();
    private static readonly Dictionary<(string Ext, string Id), Panel> Open_ = new();

    /// <summary>Raised (on the caller's thread) after a panel opens or its tree changes.</summary>
    internal static event Action<Panel>? Changed;

    /// <summary>Raised after a panel closes: (extension, id).</summary>
    internal static event Action<string, string>? Closed;

    /// <summary>
    /// Why panels cannot be shown right now, or null when the current surface can show them.
    /// Renderers that draw panels replace it; tests too.
    /// </summary>
    internal static Func<string?> Surface = () => $"panels are not available on this surface ({MuxConsole.Surface})";

    /// <summary>Sends a notification to the extension's process (default: the running process); tests replace it.</summary>
    internal static Action<string, string, object> Notify = (ext, method, payload) =>
        ExtensionHost.Running.FirstOrDefault(p => p.Name == ext && p.IsRunning)?.Notify(method, payload);

    /// <summary>The open panels, oldest first.</summary>
    internal static IReadOnlyList<Panel> All() { lock (Gate) return Open_.Values.OrderBy(p => p.Version).ToList(); }

    /// <summary>The open panel <paramref name="id"/> of <paramref name="ext"/>, or null.</summary>
    internal static Panel? Get(string ext, string id) { lock (Gate) return Open_.GetValueOrDefault((ext, id)); }

    private static int _version;

    /// <summary>Open (or re-open with a new tree) a panel. Returns null on success, else the error.</summary>
    internal static string? Open(string ext, string id, string? title, JsonElement view)
    {
        if (!IsValidId(id)) return "params.id must be 1-64 letters, digits, '-' or '_'";
        if (Surface() is { } why) return why;
        if (Parse(view, out var root) is { } error) return error;
        Panel panel;
        lock (Gate)
        {
            if (!Open_.ContainsKey((ext, id)) && Open_.Keys.Count(k => k.Ext == ext) >= MaxPanelsPerExtension)
                return $"at most {MaxPanelsPerExtension} open panels per extension";
            panel = new Panel(ext, id, Clean(title, 80) is { Length: > 0 } t ? t : id, root!, view.Clone(), Interlocked.Increment(ref _version));
            Open_[(ext, id)] = panel;
        }
        Changed?.Invoke(panel);
        return null;
    }

    /// <summary>Replace the tree of an open panel. Returns null on success, else the error.</summary>
    internal static string? Update(string ext, string id, JsonElement view)
    {
        if (Parse(view, out var root) is { } error) return error;
        Panel panel;
        lock (Gate)
        {
            if (!Open_.TryGetValue((ext, id), out var old)) return $"panel '{id}' is not open";
            panel = old with { Root = root!, View = view.Clone(), Version = Interlocked.Increment(ref _version) };
            Open_[(ext, id)] = panel;
        }
        Changed?.Invoke(panel);
        return null;
    }

    /// <summary>
    /// Close a panel. <paramref name="byUser"/>: the user closed it, so the extension gets a
    /// <c>ui/event</c> of type <c>close</c>. False when it was not open.
    /// </summary>
    internal static bool Close(string ext, string id, bool byUser)
    {
        lock (Gate) if (!Open_.Remove((ext, id))) return false;
        Closed?.Invoke(ext, id);
        if (byUser) SendEvent(ext, id, null, "close", null);
        return true;
    }

    /// <summary>Close every panel of <paramref name="ext"/> (its process stopped or exited).</summary>
    internal static void CloseAll(string ext)
    {
        List<string> ids;
        lock (Gate) ids = Open_.Keys.Where(k => k.Ext == ext).Select(k => k.Id).ToList();
        foreach (var id in ids) Close(ext, id, byUser: false);
    }

    /// <summary>Tell the extension the user acted on panel <paramref name="id"/>: press, submit, select or close.</summary>
    internal static void SendEvent(string ext, string id, string? key, string type, string? value) =>
        Notify(ext, "ui/event", new Dictionary<string, object?> { ["id"] = id, ["key"] = key, ["type"] = type, ["value"] = value });

    /// <summary>Panel ids: letters, digits, <c>-</c>, <c>_</c>, 1-64 chars.</summary>
    internal static bool IsValidId(string? id) =>
        id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Validate <paramref name="view"/> into a tree. Returns null on success, else an error naming the problem.</summary>
    internal static string? Parse(JsonElement view, out Node? root)
    {
        root = null;
        if (view.ValueKind != JsonValueKind.Object) return "params.view must be an element object, e.g. { \"type\": \"text\", \"text\": \"hi\" }";
        try { root = ParseNode(view, "view", 1, new Budget()); return null; }
        catch (FormatException ex) { return ex.Message; }
    }

    // Running totals for one tree.
    private sealed class Budget
    {
        internal int Nodes, Chars;
        internal readonly HashSet<string> Keys = new(StringComparer.Ordinal);
    }

    private static Node ParseNode(JsonElement e, string path, int depth, Budget b)
    {
        if (depth > MaxDepth) throw new FormatException($"{path}: nested deeper than {MaxDepth}");
        if (++b.Nodes > MaxNodes) throw new FormatException($"the tree has more than {MaxNodes} elements");
        if (e.ValueKind != JsonValueKind.Object) throw new FormatException($"{path}: must be an element object");
        string type = e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
        if (!Props.TryGetValue(type, out var allowed))
            throw new FormatException($"{path}: unknown element type '{type}' (use {string.Join(", ", Types)})");
        foreach (var prop in e.EnumerateObject())
            if (prop.Name != "type" && !allowed.Contains(prop.Name))
                throw new FormatException($"{path}: {type} has no prop '{prop.Name}'");

        string? Str(string name, int max)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind != JsonValueKind.String) throw new FormatException($"{path}.{name} must be a string");
            var s = v.GetString()!;
            return s.Length > max ? s[..max] : s;
        }
        bool Bool(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        string Text(string name, int max)
        {
            var s = Str(name, max) ?? "";
            if ((b.Chars += s.Length) > MaxTextChars) throw new FormatException($"the tree has more than {MaxTextChars} characters of text");
            return s;
        }
        string? Key()
        {
            var k = Str("key", 64);
            if (k is null || !IsValidId(k)) throw new FormatException($"{path}: {type} needs a key (letters, digits, '-', '_')");
            if (!b.Keys.Add(k)) throw new FormatException($"{path}: key '{k}' is used twice");
            return k;
        }

        switch (type)
        {
            case "box":
            {
                string dir = Str("dir", 8) ?? "col";
                if (dir is not ("col" or "row")) throw new FormatException($"{path}.dir must be 'col' or 'row'");
                string border = Str("border", 8) ?? "none";
                if (border is not ("none" or "single" or "round")) throw new FormatException($"{path}.border must be 'none', 'single' or 'round'");
                int gap = e.TryGetProperty("gap", out var g) && g.TryGetInt32(out var gi) ? Math.Clamp(gi, 0, 8) : 0;
                var kids = new List<Node>();
                if (e.TryGetProperty("children", out var ch))
                {
                    if (ch.ValueKind != JsonValueKind.Array) throw new FormatException($"{path}.children must be an array");
                    int i = 0;
                    foreach (var c in ch.EnumerateArray()) kids.Add(ParseNode(c, $"{path}.children[{i++}]", depth + 1, b));
                }
                return new Node("box", Dir: dir, Gap: gap, Border: border, Children: kids);
            }
            case "text":
            {
                var color = Str("color", 16);
                if (color is not null && !Colors.Contains(color)) throw new FormatException($"{path}.color must be one of {string.Join(", ", Colors)}");
                return new Node("text", Text: Text("text", MaxTextChars), Color: color, Bold: Bool("bold"), Dim: Bool("dim"));
            }
            case "button":
            {
                var key = Key();
                var hk = Str("hotkey", 1);
                if (hk is { Length: 1 } && !char.IsAsciiLetterOrDigit(hk[0])) throw new FormatException($"{path}.hotkey must be one letter or digit");
                return new Node("button", key, Text: Clean(Text("label", 80), 80) ?? "", Hotkey: hk is { Length: 1 } ? char.ToLowerInvariant(hk[0]) : null);
            }
            case "input":
                return new Node("input", Key(), Text: Text("value", 4000), Placeholder: Clean(Str("placeholder", 120), 120));
            case "list":
            {
                var key = Key();
                var items = new List<Item>();
                if (e.TryGetProperty("items", out var its))
                {
                    if (its.ValueKind != JsonValueKind.Array) throw new FormatException($"{path}.items must be an array");
                    foreach (var it in its.EnumerateArray())
                    {
                        if (it.ValueKind != JsonValueKind.Object || !it.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.String)
                            throw new FormatException($"{path}.items[{items.Count}] needs a string value");
                        string label = it.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()! : v.GetString()!;
                        string? detail = it.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                        if ((b.Chars += label.Length + (detail?.Length ?? 0)) > MaxTextChars) throw new FormatException($"the tree has more than {MaxTextChars} characters of text");
                        if (++b.Nodes > MaxNodes) throw new FormatException($"the tree has more than {MaxNodes} elements");
                        items.Add(new Item(v.GetString()!, Clean(label, 200) ?? "", Clean(detail, 200)));
                    }
                }
                return new Node("list", key, Items: items, Selected: Str("selected", 200));
            }
            case "markdown":
            case "code":
                return new Node(type, Text: Text("text", MaxTextChars));
            default: // progress
            {
                double value = e.TryGetProperty("value", out var v) && v.TryGetDouble(out var dv) ? Math.Clamp(dv, 0, 1) : 0;
                return new Node("progress", Value: value, Text: Clean(Text("label", 80), 80) ?? "");
            }
        }
    }

    // One line of plain text: control characters become spaces, capped length.
    private static string? Clean(string? text, int max)
    {
        if (text is null) return null;
        var s = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return s.Length <= max ? s : s[..max];
    }
}
