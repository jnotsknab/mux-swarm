using System.Text.Json;
using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

public class SingleOrArrayConverter<T> : JsonConverter<List<T>> where T : new()
{
    public override List<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            return JsonSerializer.Deserialize<List<T>>(ref reader, options);
        }

        var singleItem = JsonSerializer.Deserialize<T>(ref reader, options);
        return singleItem != null ? [singleItem] : [];
    }

    public override void Write(Utf8JsonWriter writer, List<T> value, JsonSerializerOptions options)
    {
        // For serialization, we'll always write an array for consistency.
        JsonSerializer.Serialize(writer, value, options);
    }
}
