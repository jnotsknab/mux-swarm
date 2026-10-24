using System.Threading;

namespace MuxSwarm.Engine;

/// <summary>
/// Raises the thread-pool floor at process start. Startup spawns every enabled MCP server at once and each stdio
/// transport parks pool threads on its pipes; with the default floor (one thread per core) the pool only grows by
/// about one thread per half second, so the servers queue behind each other. Measured: all servers ready in 8.3 s
/// with the default, 4.2 s with this floor (same servers start in 2.4 s outside Mux).
/// </summary>
internal static class StartupThreads
{
    /// <summary>Minimum worker / I/O threads requested at startup.</summary>
    internal const int Floor = 32;

    /// <summary>Raise (never lower) the pool minimums to <see cref="Floor"/>. Returns the minimums now in effect.</summary>
    internal static (int Worker, int Io) Apply()
    {
        ThreadPool.GetMinThreads(out int worker, out int io);
        int w = Math.Max(worker, Floor), i = Math.Max(io, Floor);
        if (w != worker || i != io) ThreadPool.SetMinThreads(w, i);
        ThreadPool.GetMinThreads(out worker, out io);
        return (worker, io);
    }
}
