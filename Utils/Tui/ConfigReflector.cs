using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using MuxSwarm.Utils;

namespace MuxSwarm.Utils.Tui;

/// <summary>
/// Reflection-driven config walker. Instead of hand-listing every settable key, this walks the
/// live AppConfig (config.json) and SwarmConfig (swarm.json) object graphs and exposes EVERY
/// scalar leaf as a dotted path keyed by its <see cref="JsonPropertyName"/> (e.g.
/// <c>console.collapseToolLines</c>, <c>ultra.thinkingBudget</c>, <c>serve.auth.enabled</c>,
/// <c>swarm.executionLimits.maxSubAgentIterations</c>, <c>swarm.agents.CodeAgent.model</c>).
///
/// Routing: any path beginning <c>swarm.</c> resolves against SwarmConfig and persists to
/// swarm.json; everything else resolves against AppConfig and persists to config.json.
///
/// Scope: scalar leaves only (string / bool / int / long / float / double / enum, plus their
/// Nullable variants). Object-of-objects collections (the McpServers dictionary, the agents list
/// as a whole, provider lists) are NOT walked as add/remove here - those keep their dedicated
/// wizards. Per-agent SCALARS, however, ARE reachable via <c>swarm.agents.&lt;name&gt;.&lt;field&gt;</c>.
/// </summary>
internal static class ConfigReflector
{
    public sealed record Leaf(string Path, string TypeHint, Func<string> Get, Func<string, (bool ok, string msg)> Set);

    private static bool IsScalar(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t == typeof(string) || t == typeof(bool) || t == typeof(int) || t == typeof(long)
            || t == typeof(float) || t == typeof(double) || t.IsEnum;
    }

    private static string TypeHintOf(Type t)
    {
        var u = Nullable.GetUnderlyingType(t);
        bool nullable = u != null;
        t = u ?? t;
        string baseHint =
            t == typeof(bool) ? "on|off" :
            t == typeof(int) || t == typeof(long) ? "<int>" :
            t == typeof(float) || t == typeof(double) ? "<num>" :
            t.IsEnum ? string.Join("|", Enum.GetNames(t).Select(n => n.ToLowerInvariant())) :
            "<text>";
        return nullable && t != typeof(string) ? baseHint + "|null" : baseHint;
    }

    private static string JsonName(PropertyInfo p)
        => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? char.ToLowerInvariant(p.Name[0]) + p.Name[1..];

    /// <summary>Enumerate every scalar leaf reachable from a root object, prefixing dotted paths.</summary>
    public static IEnumerable<Leaf> Walk(object? root, string prefix)
    {
        if (root is null) yield break;
        foreach (var p in root.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead) continue;
            if (p.GetIndexParameters().Length > 0) continue;
            // Skip statics-as-instance shims like ExecutionLimits.Current.
            if (p.GetMethod is { IsStatic: true }) continue;

            var pt = p.PropertyType;
            string name = JsonName(p);
            string path = prefix.Length == 0 ? name : prefix + "." + name;

            if (IsScalar(pt))
            {
                var prop = p;        // capture
                var owner = root;    // capture
                yield return new Leaf(
                    path,
                    TypeHintOf(pt),
                    () => FormatValue(prop.GetValue(owner)),
                    v => TrySet(owner, prop, v));
                continue;
            }

            // Dictionary<string, AgentConfig>-style maps are not generically walked here (only
            // the swarm agents list is, by name, below). Skip dictionaries/lists of objects.
            if (typeof(IDictionary).IsAssignableFrom(pt)) continue;

            if (typeof(IEnumerable).IsAssignableFrom(pt) && pt != typeof(string))
            {
                // Special case: swarm.agents (List<AgentConfig>) -> address each agent's scalars by name.
                if (p.GetValue(root) is IEnumerable items)
                {
                    foreach (var item in items)
                    {
                        if (item is null) continue;
                        var nameProp = item.GetType().GetProperty("Name");
                        var key = nameProp?.GetValue(item) as string;
                        if (string.IsNullOrWhiteSpace(key)) continue;
                        foreach (var leaf in Walk(item, path + "." + key))
                            yield return leaf;
                    }
                }
                continue;
            }

            // Nested config object: recurse. Materialize a default instance for null nested
            // objects so their leaves are still addressable (set creates the branch).
            object? child = p.GetValue(root);
            if (child is null)
            {
                if (!p.CanWrite) continue;
                try { child = Activator.CreateInstance(pt); p.SetValue(root, child); }
                catch { continue; }
            }
            foreach (var leaf in Walk(child, path))
                yield return leaf;
        }
    }

    private static string FormatValue(object? v)
    {
        if (v is null) return "(null)";
        if (v is bool b) return b ? "true" : "false";
        if (v is IFormattable f) return f.ToString(null, CultureInfo.InvariantCulture);
        return v.ToString() ?? "";
    }

    private static (bool ok, string msg) TrySet(object owner, PropertyInfo prop, string raw)
    {
        if (!prop.CanWrite) return (false, $"{JsonName(prop)} is read-only.");
        var t = prop.PropertyType;
        var u = Nullable.GetUnderlyingType(t);
        bool nullable = u != null || !t.IsValueType;
        var target = u ?? t;
        raw = raw.Trim();

        if (nullable && (raw.Equals("null", StringComparison.OrdinalIgnoreCase)
                         || raw.Equals("none", StringComparison.OrdinalIgnoreCase) || raw.Length == 0))
        {
            prop.SetValue(owner, null);
            return (true, $"{JsonName(prop)} = (null)");
        }

        try
        {
            object val;
            if (target == typeof(string)) val = raw;
            else if (target == typeof(bool))
            {
                var lv = raw.ToLowerInvariant();
                if (lv is "true" or "on" or "1" or "yes") val = true;
                else if (lv is "false" or "off" or "0" or "no") val = false;
                else return (false, $"{JsonName(prop)} expects a boolean (got '{raw}').");
            }
            else if (target == typeof(int))
            {
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    return (false, $"{JsonName(prop)} expects an integer (got '{raw}').");
                val = n;
            }
            else if (target == typeof(long))
            {
                if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    return (false, $"{JsonName(prop)} expects an integer (got '{raw}').");
                val = n;
            }
            else if (target == typeof(float))
            {
                if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                    return (false, $"{JsonName(prop)} expects a number (got '{raw}').");
                val = n;
            }
            else if (target == typeof(double))
            {
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                    return (false, $"{JsonName(prop)} expects a number (got '{raw}').");
                val = n;
            }
            else if (target.IsEnum)
            {
                var match = Enum.GetNames(target).FirstOrDefault(n => n.Equals(raw, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                    return (false, $"{JsonName(prop)} expects one of: {string.Join("|", Enum.GetNames(target).Select(n => n.ToLowerInvariant()))}.");
                val = Enum.Parse(target, match);
            }
            else return (false, $"{JsonName(prop)} has unsupported type {target.Name}.");

            prop.SetValue(owner, val);
            return (true, $"{JsonName(prop)} = {FormatValue(val)}");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to set {JsonName(prop)}: {ex.Message}");
        }
    }
}
