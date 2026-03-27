using System.Text.Json.Serialization;
using OpenTelemetry.Exporter;

namespace MuxSwarm.Utils;

public class TelemetryConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("endpoint")] public string? Endpoint { get; set; }
    [JsonPropertyName("protocol")] public string? Protocol { get; set; }
    [JsonPropertyName("serviceName")] public string? ServiceName { get; set; }
    [JsonPropertyName("logLevel")] public string? LogLevel { get; set; }
    [JsonPropertyName("headers")] public Dictionary<string, string>? Headers { get; set; }
    [JsonPropertyName("verbosity")] public string? Verbosity { get; set; } // minimal, standard, verbose

    [JsonIgnore]
    public TelemetryVerbosity VerbosityLevel => (Verbosity?.ToLowerInvariant()) switch
    {
        "minimal" => TelemetryVerbosity.Minimal,
        "verbose" => TelemetryVerbosity.Verbose,
        _ => TelemetryVerbosity.Standard
    };

    [JsonIgnore]
    public OtlpExportProtocol ExportProtocol => (Protocol?.ToLowerInvariant()) switch
    {
        "grpc" => OtlpExportProtocol.Grpc,
        "http" or "httpprotobuf" or "http/protobuf" => OtlpExportProtocol.HttpProtobuf,
        _ => OtlpExportProtocol.Grpc
    };
}

public enum TelemetryVerbosity
{
    Minimal,
    Standard, 
    Verbose
}