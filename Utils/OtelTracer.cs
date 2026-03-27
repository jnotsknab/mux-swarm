using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace MuxSwarm.Utils;

public static class OtelTracer
{   
    private static readonly ActivitySource Source = new("MuxSwarm");
    private static TracerProvider? _provider;
    
    public static ActivitySource GetSource() => Source;

    public static bool TryInit()
    {
        if (!App.Config.Telemetry.Enabled || string.IsNullOrEmpty(App.Config.Telemetry.Endpoint))
            return false;

        _provider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault()
                .AddService(App.Config.Telemetry.ServiceName ?? "mux-swarm")
                .AddAttributes(new Dictionary<string, object>
                {
                    { "host.name", Environment.MachineName },
                    { "os.type", RuntimeInformation.OSDescription },
                    { "service.version", App.Version },
                    { "service.instance.id", App.ServePort > 0 
                        ? $"{Environment.MachineName}:{App.ServePort}" 
                        : Environment.MachineName }
                }))
            .AddSource("MuxSwarm")
            .AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri(App.Config.Telemetry.Endpoint);
                options.Protocol = App.Config.Telemetry.ExportProtocol;

                if (App.Config.Telemetry.Headers is { Count: > 0 })
                    options.Headers = string.Join(",", App.Config.Telemetry.Headers.Select(h => $"{h.Key}={h.Value}"));
            })
            .Build();

        return true;
    }

    public static void Shutdown()
    {
        _provider?.ForceFlush();
        _provider?.Dispose();
        _provider = null;
    }
}