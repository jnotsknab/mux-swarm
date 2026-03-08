using System.Text.Json;
using MuxSwarm.State;

namespace MuxSwarm.State;

/// <summary>
/// Manages persistent state for continuous mode execution.
/// Provides atomic read/write of CurrentStateMetadata to a known path
/// so that process crashes can be recovered and loops resumed cleanly.
/// </summary>
public static class ContinuousStateManager
{
    private const string StateDirName = "state";

    private static string GetStatePath(string goalId, string sessionDir)
    {
        var stateDir = Path.Combine(sessionDir, StateDirName);
        Directory.CreateDirectory(stateDir);
        return Path.Combine(stateDir, $"{goalId}.json");
    }

    /// <summary>
    /// Loads state for a given goal ID. Returns null if no state exists.
    /// </summary>
    public static CurrentStateMetadata? Load(string goalId, string sessionDir)
    {
        var path = GetStatePath(goalId, sessionDir);

        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<CurrentStateMetadata>(json);

            if (state != null)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[CONTINUOUS] Resumed state for goal '{goalId}' " +
                                  $"— iteration {state.Iteration}, " +
                                  $"last completed {state.LastCompletedAt:yyyy-MM-dd HH:mm:ss}");
                Console.ResetColor();
            }

            return state;
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[CONTINUOUS] Failed to load state for '{goalId}': {ex.Message}");
            Console.ResetColor();
            throw;
        }
    }

    /// <summary>
    /// Atomically writes state to disk. Writes to .tmp first then moves
    /// so readers never see a partial write.
    /// </summary>
    public static void WriteAtomic(string goalId, CurrentStateMetadata state, string sessionDir)
    {
        var path = GetStatePath(goalId, sessionDir);
        var tmp = path + ".tmp";

        try
        {
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[CONTINUOUS] Failed to write state for '{goalId}': {ex.Message}");
            Console.ResetColor();
            throw;
        }
    }

    /// <summary>
    /// Marks state as stopped on graceful shutdown.
    /// </summary>
    public static void MarkStopped(string goalId, CurrentStateMetadata state, string sessionDir)
    {
        state.Status = "stopped";
        WriteAtomic(goalId, state, sessionDir);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[CONTINUOUS] State for '{goalId}' marked as stopped at iteration {state.Iteration}.");
        Console.ResetColor();
    }

    /// <summary>
    /// Deletes state file on intentional goal completion / teardown.
    /// </summary>
    public static void Clear(string goalId, string sessionDir)
    {
        var path = GetStatePath(goalId, sessionDir);

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[CONTINUOUS] Failed to clear state for '{goalId}': {ex.Message}");
            Console.ResetColor();
            throw;
        }
    }
}