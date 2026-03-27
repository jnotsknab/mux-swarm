using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace MuxSwarm.Utils;

public static class OtelMetrics
{
    private static readonly Meter Meter = new("MuxSwarm");
    private static MeterProvider? _provider;

    // Token economics
    public static readonly Counter<long> TokensInput = Meter.CreateCounter<long>("mux.tokens.input");
    public static readonly Counter<long> TokensOutput = Meter.CreateCounter<long>("mux.tokens.output");
    public static readonly Counter<long> TokensTotal = Meter.CreateCounter<long>("mux.tokens.total");
    public static readonly Counter<long> TokensCompacted = Meter.CreateCounter<long>("mux.tokens.compacted");

    // Agent lifecycle
    public static readonly Counter<long> AgentTurns = Meter.CreateCounter<long>("mux.agent.turns");
    public static readonly Counter<long> AgentErrors = Meter.CreateCounter<long>("mux.agent.errors");
    public static readonly Counter<long> AgentStuckCount = Meter.CreateCounter<long>("mux.agent.stuck");
    public static readonly Histogram<double> AgentTurnDuration = Meter.CreateHistogram<double>("mux.agent.turn_duration_ms");

    // Orchestration
    public static readonly Counter<long> Delegations = Meter.CreateCounter<long>("mux.orchestrator.delegations");
    public static readonly Counter<long> OrchestratorIterations = Meter.CreateCounter<long>("mux.orchestrator.iterations");
    public static readonly Counter<long> SubTaskRetries = Meter.CreateCounter<long>("mux.orchestrator.retries");
    public static readonly Histogram<double> DelegationDuration = Meter.CreateHistogram<double>("mux.orchestrator.delegation_duration_ms");

    // Tool calls
    public static readonly Counter<long> ToolCalls = Meter.CreateCounter<long>("mux.tool.calls");
    public static readonly Counter<long> ToolErrors = Meter.CreateCounter<long>("mux.tool.errors");
    public static readonly Histogram<double> ToolCallDuration = Meter.CreateHistogram<double>("mux.tool.duration_ms");

    // Sessions
    public static readonly Counter<long> SessionsStarted = Meter.CreateCounter<long>("mux.session.started");
    public static readonly Counter<long> SessionsCompleted = Meter.CreateCounter<long>("mux.session.completed");
    public static readonly Counter<long> SessionsFailed = Meter.CreateCounter<long>("mux.session.failed");
    public static readonly Histogram<double> SessionDuration = Meter.CreateHistogram<double>("mux.session.duration_ms");

    // Goals
    public static readonly Counter<long> GoalsReceived = Meter.CreateCounter<long>("mux.goal.received");
    public static readonly Counter<long> GoalsCompleted = Meter.CreateCounter<long>("mux.goal.completed");
    public static readonly Counter<long> GoalsFailed = Meter.CreateCounter<long>("mux.goal.failed");

    // Compaction
    public static readonly Counter<long> CompactionRuns = Meter.CreateCounter<long>("mux.compaction.runs");
    public static readonly Histogram<double> CompactionDuration = Meter.CreateHistogram<double>("mux.compaction.duration_ms");
    public static readonly Histogram<double> CompactionRatio = Meter.CreateHistogram<double>("mux.compaction.ratio");

    // Memory operations
    public static readonly Counter<long> MemoryReads = Meter.CreateCounter<long>("mux.memory.reads");
    public static readonly Counter<long> MemoryWrites = Meter.CreateCounter<long>("mux.memory.writes");

    // Daemon
    public static readonly Counter<long> DaemonTriggersFired = Meter.CreateCounter<long>("mux.daemon.triggers_fired");
    public static readonly Counter<long> BridgeRestarts = Meter.CreateCounter<long>("mux.daemon.bridge_restarts");

    public static bool TryInit()
    {
        if (!App.Config.Telemetry.Enabled || string.IsNullOrEmpty(App.Config.Telemetry.Endpoint))
            return false;

        _provider = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(App.Config.Telemetry.ServiceName ?? "mux-swarm"))
            .AddMeter("MuxSwarm")
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

    // Convenience methods that respect verbosity

    public static void RecordTokens(string agent, string model, long input, long output)
    {
        var tags = new TagList { { "agent", agent } };

        if (App.Config.Telemetry.VerbosityLevel >= TelemetryVerbosity.Standard)
            tags.Add("model", model);

        TokensInput.Add(input, tags);
        TokensOutput.Add(output, tags);
        TokensTotal.Add(input + output, tags);
    }

    public static void RecordToolCall(string agent, string tool, double durationMs, bool success, string? args = null, string? result = null)
    {
        var tags = new TagList { { "agent", agent }, { "success", success } };

        if (App.Config.Telemetry.VerbosityLevel >= TelemetryVerbosity.Standard)
            tags.Add("tool", tool);

        ToolCalls.Add(1, tags);
        ToolCallDuration.Record(durationMs, tags);

        if (!success)
            ToolErrors.Add(1, tags);

        // Verbose: attach full payloads as span events
        if (App.Config.Telemetry.VerbosityLevel >= TelemetryVerbosity.Verbose)
        {
            var eventTags = new ActivityTagsCollection
            {
                { "agent", agent },
                { "tool", tool },
                { "success", success },
                { "duration_ms", durationMs }
            };
            if (args != null) eventTags.Add("args", Truncate(args, 4096));
            if (result != null) eventTags.Add("result", Truncate(result, 4096));

            Activity.Current?.AddEvent(new ActivityEvent("tool_call_detail", tags: eventTags));
        }
    }

    public static void RecordAgentTurn(string agent, string model, double durationMs, long inputTokens, long outputTokens)
    {
        var tags = new TagList { { "agent", agent } };

        if (App.Config.Telemetry.VerbosityLevel >= TelemetryVerbosity.Standard)
            tags.Add("model", model);

        AgentTurns.Add(1, tags);
        AgentTurnDuration.Record(durationMs, tags);
        RecordTokens(agent, model, inputTokens, outputTokens);
    }

    public static void RecordAgentMessage(string agent, string role, string content)
    {
        if (App.Config.Telemetry.VerbosityLevel < TelemetryVerbosity.Verbose)
            return;

        Activity.Current?.AddEvent(new ActivityEvent("message", tags: new ActivityTagsCollection
        {
            { "agent", agent },
            { "role", role },
            { "content", Truncate(content, 8192) }
        }));
    }

    public static void RecordDelegation(string from, string to, string task, double durationMs)
    {
        var tags = new TagList();

        if (App.Config.Telemetry.VerbosityLevel >= TelemetryVerbosity.Standard)
        {
            tags.Add("from", from);
            tags.Add("to", to);
        }

        Delegations.Add(1, tags);
        DelegationDuration.Record(durationMs, tags);

        if (App.Config.Telemetry.VerbosityLevel >= TelemetryVerbosity.Verbose)
        {
            Activity.Current?.AddEvent(new ActivityEvent("delegation_detail", tags: new ActivityTagsCollection
            {
                { "from", from },
                { "to", to },
                { "task", Truncate(task, 4096) }
            }));
        }
    }

    private static string Truncate(string value, int maxLength)
        => value.Length > maxLength ? value[..maxLength] + "...[truncated]" : value;
}