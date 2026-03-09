using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

/// <summary>
/// Optional model tuning parameters. All fields are nullable —
/// omitted values are left to the provider's defaults.
/// </summary>
public class ModelOpts
{
    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("topP")]
    public float? TopP { get; set; }

    [JsonPropertyName("topK")]
    public int? TopK { get; set; }

    [JsonPropertyName("maxOutputTokens")]
    public int? MaxOutputTokens { get; set; }

    [JsonPropertyName("frequencyPenalty")]
    public float? FrequencyPenalty { get; set; }

    [JsonPropertyName("presencePenalty")]
    public float? PresencePenalty { get; set; }

    [JsonPropertyName("seed")]
    public long? Seed { get; set; }

    /// <summary>
    /// Builds a ChatOptions from non-null fields.
    /// Returns null if every field is default.
    /// </summary>
    public Microsoft.Extensions.AI.ChatOptions? ToChatOptions()
    {
        if (Temperature is null && TopP is null && TopK is null &&
            MaxOutputTokens is null && FrequencyPenalty is null &&
            PresencePenalty is null && Seed is null)
            return null;

        return new Microsoft.Extensions.AI.ChatOptions
        {
            Temperature = Temperature,
            TopP = TopP,
            TopK = TopK,
            MaxOutputTokens = MaxOutputTokens,
            FrequencyPenalty = FrequencyPenalty,
            PresencePenalty = PresencePenalty,
            Seed = Seed
        };
    }
}