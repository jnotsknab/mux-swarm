namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Serializes tests that mutate global console state (MuxConsole.StdioMode and the
/// redirected Console.Out). xunit runs test classes in parallel by default; these
/// classes share process-wide statics, so they must run in a single non-parallel
/// collection to avoid cross-test output capture races.
/// </summary>
[CollectionDefinition("ConsoleState", DisableParallelization = true)]
public class ConsoleStateCollection;
