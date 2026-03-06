using System.Text.Json.Serialization;

namespace MuxSwarm.Utils;

public class LlmProvidersConfig
{
    [JsonPropertyName("openAiCompatible")]
    public LlmProviderConfig OpenApiCompatible { get; set; } = new();

    [JsonPropertyName("ollama")]
    public LlmProviderConfig Ollama { get; set; } = new();
}