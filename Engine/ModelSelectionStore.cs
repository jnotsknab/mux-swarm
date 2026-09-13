using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MuxSwarm.Engine;

/// <summary>Targeted model/effort edits that preserve unrelated and unknown Swarm.json fields.</summary>
internal static class ModelSelectionStore
{
    private static readonly object WriteLock = new();

    /// <summary>Stable slot identity, label, and persisted model/effort at picker-open time.</summary>
    internal sealed record Slot(string Id, string Label, string? Model, string? Effort);

    /// <summary>An immutable edit baseline; saving fails if the file changed since this snapshot.</summary>
    internal sealed record Snapshot(string Path, byte[] Bytes, IReadOnlyList<Slot> Slots);

    /// <summary>Load the same slot scope as the existing CLI picker without accessing provider credentials.</summary>
    internal static Snapshot Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var root = Parse(bytes);
        var slots = new List<Slot>();
        void Add(string id, string label, JsonObject node)
        {
            slots.Add(new(id, label, node["model"]?.GetValue<string>(),
                node["modelOpts"]?["reasoning"]?["effort"]?.GetValue<string>()));
        }
        if (root["compactionAgent"] is JsonObject compaction) Add("compactionAgent", "CompactionAgent", compaction);
        if (root["singleAgent"] is JsonObject single)
            Add("singleAgent", single["name"]?.GetValue<string>() is { Length: > 0 } name ? name : "SingleAgent", single);
        if (root["orchestrator"] is JsonObject orchestrator) Add("orchestrator", "Orchestrator", orchestrator);
        if (root["agents"] is JsonArray agents)
            for (int i = 0; i < agents.Count; i++)
                if (agents[i] is JsonObject agent)
                    Add($"agents/{i}", agent["name"]?.GetValue<string>() ?? $"Agent {i + 1}", agent);
        return new(path, bytes, slots);
    }

    /// <summary>Persist one explicitly applied slot, returning the reloaded typed config for future runs.
    /// Null effort removes only the effort override, retaining reasoning output and other options.</summary>
    internal static SwarmConfig Save(Snapshot snapshot, string slotId, string model, string? effort)
    {
        model = model.Trim();
        if (model.Length == 0 || model.Length > 1024 || model.Any(char.IsControl))
            throw new ArgumentException("Enter a nonempty model ID without control characters.", nameof(model));
        if (effort is not null)
        {
            if (!ReasoningEffortControl.TryParse(effort, out var selection))
                throw new ArgumentException("Choose a supported effort label or custom value.", nameof(effort));
            effort = selection!.Label;
        }
        if (!snapshot.Slots.Any(s => s.Id == slotId)) throw new ArgumentException("The selected model slot no longer exists.");
        lock (WriteLock)
        {
            if (!File.ReadAllBytes(snapshot.Path).AsSpan().SequenceEqual(snapshot.Bytes))
                throw new IOException("Swarm.json changed while the picker was open. Close and reopen it before applying.");
            var root = Parse(snapshot.Bytes);
            JsonObject target = slotId.StartsWith("agents/", StringComparison.Ordinal)
                ? (JsonObject)root["agents"]![int.Parse(slotId[7..])]!
                : (JsonObject)root[slotId]!;
            target["model"] = model;
            if (effort is not null)
            {
                if (target["modelOpts"] is not null and not JsonObject)
                    throw new JsonException("modelOpts must be an object.");
                var options = target["modelOpts"] as JsonObject;
                if (options is null) target["modelOpts"] = options = new JsonObject();
                if (options["reasoning"] is not null and not JsonObject)
                    throw new JsonException("reasoning must be an object.");
                var reasoning = options["reasoning"] as JsonObject;
                if (reasoning is null) options["reasoning"] = reasoning = new JsonObject();
                reasoning["effort"] = effort;
            }
            else if (target["modelOpts"]?["reasoning"] is JsonObject reasoning)
                reasoning.Remove("effort");
            string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            var config = JsonSerializer.Deserialize<SwarmConfig>(json)
                ?? throw new JsonException("Swarm.json must contain an object.");
            string temporary = snapshot.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                bool bom = snapshot.Bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF });
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (bom) stream.Write(new byte[] { 0xEF, 0xBB, 0xBF });
                    stream.Write(Encoding.UTF8.GetBytes(json));
                    stream.Flush(flushToDisk: true);
                }
                // Detect external edits during serialization as well; same-volume rename avoids partial JSON.
                if (!File.ReadAllBytes(snapshot.Path).AsSpan().SequenceEqual(snapshot.Bytes))
                    throw new IOException("Swarm.json changed while applying. Reopen the picker.");
                File.Move(temporary, snapshot.Path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return config;
        }
    }

    private static JsonObject Parse(byte[] bytes)
    {
        int offset = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        return JsonNode.Parse(Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset)) as JsonObject
            ?? throw new JsonException("Swarm.json must contain an object.");
    }
}
