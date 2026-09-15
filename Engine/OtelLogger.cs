using System.Diagnostics;

namespace MuxSwarm.Engine;

public static class OtelLogger
{
    public static void Warn(string message)
    {
        Telemetry.OtelIngest.RecordLog("warn", message);
        Activity.Current?.AddEvent(new ActivityEvent("log",
            tags: new ActivityTagsCollection
            {
                { "level", "warn" },
                { "log.severity", "WARN" },
                { "message", message }
            }));
    }

    public static void Info(string message)
    {
        Telemetry.OtelIngest.RecordLog("info", message);
        Activity.Current?.AddEvent(new ActivityEvent("log",
            tags: new ActivityTagsCollection
            {
                { "level", "info" },
                { "log.severity", "INFO" },
                { "message", message }
            }));
    }

    public static void Error(string message)
    {
        Telemetry.OtelIngest.RecordLog("error", message);
        var current = Activity.Current;
        current?.AddEvent(new ActivityEvent("log",
            tags: new ActivityTagsCollection
            {
                { "level", "error" },
                { "log.severity", "ERROR" },
                { "message", message }
            }));
        current?.SetStatus(ActivityStatusCode.Error, message);
    }
}