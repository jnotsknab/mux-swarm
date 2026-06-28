namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Serializes tests that mutate the process-wide ExecutionLimits.Current singleton (e.g. the
/// subAgentSummaryMode compaction-policy tests), so a concurrent test class cannot observe a
/// transiently-swapped value mid-run.
/// </summary>
[CollectionDefinition("ExecLimitsState", DisableParallelization = true)]
public class ExecLimitsStateCollection;
