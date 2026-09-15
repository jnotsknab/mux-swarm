namespace MuxSwarm.Tests.Tests;

// TelemetrySink/CostLedger/OtelIngest are process-static accumulators; classes that write
// through them must not run in parallel with each other (sink events bleed across the
// per-test OverrideDirectoryForTests dirs). Same pattern as ConsoleState.
[CollectionDefinition("TelemetryState", DisableParallelization = true)]
public class TelemetryStateCollection { }
