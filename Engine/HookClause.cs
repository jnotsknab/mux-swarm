using System.Text.Json;
using System.Text.Json.Serialization;

namespace MuxSwarm.Engine;

public class HookClause
{
    /// <summary>Event name(s) the hook fires on. JSON accepts a single string (<c>"turn_end"</c>) or a
    /// list (<c>["turn_end", "task_complete"]</c>); the hook fires once for any listed event. One event is
    /// written back as a plain string so existing configs round-trip unchanged.</summary>
    [JsonPropertyName("event")]
    [JsonConverter(typeof(HookEventListConverter))]
    public List<string> Events { get; set; } = [];

    [JsonPropertyName("agent")]
    public string? Agent { get; set; }

    [JsonPropertyName("tool")]
    public string? Tool { get; set; }
}

/// <summary>Reads <c>when.event</c> as a string or an array of strings; writes one event as a string.</summary>
public sealed class HookEventListConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return [];
        if (reader.TokenType == JsonTokenType.String) return [reader.GetString()!];
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("hooks[].when.event must be a string or an array of strings.");

        var list = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("hooks[].when.event array entries must be strings.");
            list.Add(reader.GetString()!);
        }
        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        if (value.Count == 1) { writer.WriteStringValue(value[0]); return; }
        writer.WriteStartArray();
        foreach (var ev in value) writer.WriteStringValue(ev);
        writer.WriteEndArray();
    }
}