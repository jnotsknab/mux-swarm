using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MuxSwarm.Engine;

/// <summary>Idle-session pruning transaction: plan, recoverable snapshot, then install typed context.</summary>
internal static class ContextPruneSession
{
    internal sealed record Result(ContextPruner.Plan Plan, string? RecoveryPath);

    /// <summary>Apply only after a complete pre-prune snapshot has been atomically published.
    /// No model request is made. Caller must own the idle session; no-op and snapshot failure leave it untouched.</summary>
    internal static async Task<Result> ApplyAsync(AIAgent agent, AgentSession session, ContextPruner.Mode mode,
        string sandbox, IReadOnlyList<string> allowed, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!session.TryGetInMemoryChatHistory(out var history) || history is null)
            throw new InvalidOperationException("This session does not expose local typed history for pruning.");
        var originals = history.ToArray();
        var plan = ContextPruner.Create(originals, mode);
        if (plan.Blocks == 0) return new Result(plan, null);
        var serialized = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Fail closed if ownership assumptions were violated while serialization was running.
        if (!session.TryGetInMemoryChatHistory(out var current) || current is null || current.Count != originals.Length
            || !current.SequenceEqual(originals, ReferenceEqualityComparer.Instance))
            throw new InvalidOperationException("Session changed while preparing pruning. Retry at its idle prompt.");
        string recovery = SaveSnapshot(serialized, sandbox, allowed);
        cancellationToken.ThrowIfCancellationRequested();
        session.SetInMemoryChatHistory(plan.Messages.ToList());
        return new Result(plan, recovery);
    }

    /// <summary>Publish the original serialized session under an allowed sandbox without overwriting any file.
    /// Non-json suffix avoids changing legacy session discovery. Existing symlink/junction ancestors are rejected.</summary>
    internal static string SaveSnapshot(JsonElement serialized, string sandbox, IReadOnlyList<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(sandbox)) throw new IOException("Configure a sandbox for prune recovery snapshots.");
        string root = Path.GetFullPath(sandbox);
        if (!allowed.Any(a => !string.IsNullOrWhiteSpace(a) && ServeMode.SafeJoin(a, root) is not null))
            throw new UnauthorizedAccessException("Prune recovery sandbox is outside allowed paths.");
        string dir = Path.Combine(root, "prune-recovery");
        void VerifyAncestors()
        {
            for (var ancestor = new DirectoryInfo(dir); ancestor is not null; ancestor = ancestor.Parent)
                if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Prune recovery paths must not traverse symbolic links or junctions.");
        }
        VerifyAncestors();
        Directory.CreateDirectory(dir);
        VerifyAncestors();
        string destination = Path.Combine(dir, $"pre-prune-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.muxprune");
        string temporary = destination + ".partial";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file, new UTF8Encoding(false), leaveOpen: true))
            {
                writer.Write(serialized.GetRawText());
                writer.Flush(); file.Flush(true);
            }
            File.Move(temporary, destination, overwrite: false);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
