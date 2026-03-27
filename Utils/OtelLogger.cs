using System.Diagnostics;

namespace MuxSwarm.Utils;

public static class OtelLogger
{   
    public static void Warn(string message)
    {
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