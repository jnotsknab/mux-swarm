using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using MuxSwarm.State;
using MuxSwarm.Utils.Teams;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// M-B: TaskDecomposer turns a light-model JSON task graph into real board tasks with blockedBy
/// edges, rejects cycles, and degrades gracefully when no model/board is available.
/// </summary>
public class TaskDecomposeTests
{
    private static string TempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mux-decompose-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Canned client returns a fixed reply, ignoring the prompt (no network).
    private sealed class CannedClient : IChatClient
    {
        private readonly string _reply;
        public CannedClient(string reply) => _reply = reply;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken ct = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _reply)));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task Decompose_ValidGraph_CreatesTasksWithBlockedByEdges()
    {
        var board = TaskBoard.Open("t", TempRoot());
        var json = """
        [
          {"index":1,"subject":"design","description":"draft","assignee":"CodeAgent","dependsOn":[]},
          {"index":2,"subject":"build","description":"impl","assignee":"CodeAgent","dependsOn":[1]},
          {"index":3,"subject":"test","description":"verify","assignee":"CodeAgent","dependsOn":[2]}
        ]
        """;
        var result = await TaskDecomposer.DecomposeAsync(
            board, "ship feature", new[] { "CodeAgent" }, new CannedClient(json), null, 12, CancellationToken.None);

        var rows = board.Snapshot();
        Assert.Equal(3, rows.Count);

        // First task has no deps and is Pending; the dependents are Blocked and reference real ids.
        var design = rows.First(r => r.Subject == "design");
        var build = rows.First(r => r.Subject == "build");
        var test = rows.First(r => r.Subject == "test");
        Assert.Empty(design.BlockedBy);
        Assert.Contains(design.Id, build.BlockedBy);
        Assert.Contains(build.Id, test.BlockedBy);
        Assert.Equal(TeamTaskStatus.Blocked, build.Status);
        Assert.Contains("Added 3 subtask", result);
    }

    [Fact]
    public async Task Decompose_ToleratesProseAndCodeFence()
    {
        var board = TaskBoard.Open("t", TempRoot());
        var reply = "Sure! Here is the plan:\n```json\n[{\"index\":1,\"subject\":\"only\",\"description\":\"d\",\"dependsOn\":[]}]\n```\nDone.";
        await TaskDecomposer.DecomposeAsync(
            board, "goal", Array.Empty<string>(), new CannedClient(reply), null, 12, CancellationToken.None);
        Assert.Single(board.Snapshot());
        Assert.Equal("only", board.Snapshot()[0].Subject);
    }

    [Fact]
    public async Task Decompose_CycleRejected_NoTasksCreated()
    {
        var board = TaskBoard.Open("t", TempRoot());
        var json = """
        [
          {"index":1,"subject":"a","description":"d","dependsOn":[2]},
          {"index":2,"subject":"b","description":"d","dependsOn":[1]}
        ]
        """;
        var result = await TaskDecomposer.DecomposeAsync(
            board, "g", Array.Empty<string>(), new CannedClient(json), null, 12, CancellationToken.None);
        Assert.Empty(board.Snapshot());
        Assert.Contains("cycle", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Decompose_RespectsMaxSubtasksCap()
    {
        var board = TaskBoard.Open("t", TempRoot());
        var items = string.Join(",", Enumerable.Range(1, 20)
            .Select(i => $"{{\"index\":{i},\"subject\":\"s{i}\",\"description\":\"d\",\"dependsOn\":[]}}"));
        var json = "[" + items + "]";
        await TaskDecomposer.DecomposeAsync(
            board, "g", Array.Empty<string>(), new CannedClient(json), null, maxSubtasks: 5, CancellationToken.None);
        Assert.Equal(5, board.Snapshot().Count);
    }

    [Fact]
    public async Task Decompose_NoClient_ReturnsGracefulMessage()
    {
        var board = TaskBoard.Open("t", TempRoot());
        var result = await TaskDecomposer.DecomposeAsync(
            board, "g", Array.Empty<string>(), null, null, 12, CancellationToken.None);
        Assert.Empty(board.Snapshot());
        Assert.Contains("model", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Decompose_UnknownAssignee_FallsBackToNull()
    {
        var board = TaskBoard.Open("t", TempRoot());
        var json = """[{"index":1,"subject":"x","description":"d","assignee":"Ghost","dependsOn":[]}]""";
        await TaskDecomposer.DecomposeAsync(
            board, "g", new[] { "CodeAgent" }, new CannedClient(json), null, 12, CancellationToken.None);
        Assert.Null(board.Snapshot()[0].Assignee);
    }

    [Fact]
    public async Task Decompose_GarbageReply_NoTasks()
    {
        var board = TaskBoard.Open("t", TempRoot());
        var result = await TaskDecomposer.DecomposeAsync(
            board, "g", Array.Empty<string>(), new CannedClient("I cannot help with that."), null, 12, CancellationToken.None);
        Assert.Empty(board.Snapshot());
        Assert.Contains("no usable subtasks", result, StringComparison.OrdinalIgnoreCase);
    }
}
