using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
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

    [JsonPropertyName("additionalParams")]
    public Dictionary<string, object>? AdditionalParams { get; set; }
    
    [JsonPropertyName("reasoning")]
    public ReasoningConfig? Reasoning { get; set; }
    
    /// <summary>
    /// Builds a ChatOptions from non-null fields.
    /// Returns null if every field is default.
    /// </summary>
    public ChatOptions? ToChatOptions()
    {
        if (Temperature is null && TopP is null && TopK is null &&
            MaxOutputTokens is null && FrequencyPenalty is null &&
            PresencePenalty is null && Seed is null && AdditionalParams is null)
            return null;
        
        ChatOptions opts = new ChatOptions
        {
            Temperature = Temperature,
            TopP = TopP,
            TopK = TopK,
            MaxOutputTokens = MaxOutputTokens,
            FrequencyPenalty = FrequencyPenalty,
            PresencePenalty = PresencePenalty,
            Seed = Seed
        };
        
        if (Reasoning is not null)
        {
            opts.Reasoning = new ReasoningOptions
            {
                Effort = Reasoning.Effort?.ToLowerInvariant() switch
                {
                    "none" => ReasoningEffort.None,
                    "low" => ReasoningEffort.Low,
                    "med" => ReasoningEffort.Medium,
                    "medium" => ReasoningEffort.Medium,
                    "high" => ReasoningEffort.High,
                    "extrahigh" => ReasoningEffort.ExtraHigh,
                    "extra_high" => ReasoningEffort.ExtraHigh,
                    _ => null
                },
                Output = Reasoning.Output?.ToLowerInvariant() switch
                {
                    "none" => ReasoningOutput.None,
                    "summary" => ReasoningOutput.Summary,
                    "full" => ReasoningOutput.Full,
                    _ => null
                }
                
            };
        }

        if (AdditionalParams is { Count: > 0 })
        {
            opts.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            foreach (var (k, v) in AdditionalParams)
                opts.AdditionalProperties[k] = v;
        }

        return opts;
    }
}