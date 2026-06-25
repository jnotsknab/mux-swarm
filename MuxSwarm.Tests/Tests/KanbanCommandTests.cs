using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MuxSwarm.State;
using MuxSwarm.Utils.Teams;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// M3 (g12.17): the /kanban editable-board command. Covers the pure parse/status/bucket/render
/// surface (the Run() shell is a thin TUI wrapper over these + the live board).
/// </summary>
public class KanbanCommandTests
{
    [Theory]
    [InlineData("/kanban", KanbanCommand.Action.Show)]
    [InlineData("/kanban init", KanbanCommand.Action.Show)]
    [InlineData("/kanban show", KanbanCommand.Action.Show)]
    [InlineData("/kanban help", KanbanCommand.Action.Help)]
    [InlineData("/kanban add fix the bug", KanbanCommand.Action.Add)]
    [InlineData("/kanban assign t1 CodeAgent", KanbanCommand.Action.Assign)]
    [InlineData("/kanban block t2 t1", KanbanCommand.Action.Block)]
    [InlineData("/kanban ready t2", KanbanCommand.Action.Ready)]
    [InlineData("/kanban move t1 done", KanbanCommand.Action.Move)]
    [InlineData("/kanban remove t1", KanbanCommand.Action.Remove)]
    [InlineData("/kanban clear", KanbanCommand.Action.Clear)]
    [InlineData("/kanban peer on", KanbanCommand.Action.Peer)]
    [InlineData("/kanban frobnicate", KanbanCommand.Action.Unknown)]
    public void Parse_RecognizesActions(string raw, KanbanCommand.Action expected)
    {
        Assert.Equal(expected, KanbanCommand.Parse(raw).Action);
    }

    [Fact]
    public void Parse_Add_CapturesFullSubject()
    {
        var p = KanbanCommand.Parse("/kanban add wire up the login flow");
        Assert.Equal(KanbanCommand.Action.Add, p.Action);
        Assert.Equal("wire up the login flow", p.Rest);
    }

    [Fact]
    public void Parse_Assign_CapturesIdAndMember()
    {
        var p = KanbanCommand.Parse("/kanban assign t3 WebAgent");
        Assert.Equal("t3", p.Arg1);
        Assert.Equal("WebAgent", p.Arg2);
    }

    [Theory]
    [InlineData("todo", TeamTaskStatus.Pending)]
    [InlineData("pending", TeamTaskStatus.Pending)]
    [InlineData("blocked", TeamTaskStatus.Blocked)]
    [InlineData("inprogress", TeamTaskStatus.InProgress)]
    [InlineData("wip", TeamTaskStatus.InProgress)]
    [InlineData("done", TeamTaskStatus.Done)]
    [InlineData("failed", TeamTaskStatus.Failed)]
    public void ParseStatus_MapsKnownWords(string word, TeamTaskStatus expected)
    {
        Assert.Equal(expected, KanbanCommand.ParseStatus(word));
    }

    [Fact]
    public void ParseStatus_UnknownReturnsNull()
    {
        Assert.Null(KanbanCommand.ParseStatus("nonsense"));
    }

    [Fact]
    public void Columns_AreFiveInWorkflowOrder()
    {
        Assert.Equal(
            new[] { TeamTaskStatus.Pending, TeamTaskStatus.Blocked, TeamTaskStatus.InProgress,
                    TeamTaskStatus.Done, TeamTaskStatus.Failed },
            KanbanCommand.Columns);
    }

    [Fact]
    public void Bucket_GroupsTasksByStatus()
    {
        var tasks = new List<TeamTask>
        {
            new() { Id = "t1", Subject = "a", Status = TeamTaskStatus.Pending },
            new() { Id = "t2", Subject = "b", Status = TeamTaskStatus.Done },
            new() { Id = "t3", Subject = "c", Status = TeamTaskStatus.Pending },
        };
        var buckets = KanbanCommand.Bucket(tasks);
        var todo = buckets.First(b => b.Status == TeamTaskStatus.Pending).Tasks;
        var done = buckets.First(b => b.Status == TeamTaskStatus.Done).Tasks;
        Assert.Equal(2, todo.Count);
        Assert.Single(done);
        Assert.Empty(buckets.First(b => b.Status == TeamTaskStatus.Failed).Tasks);
    }

    [Fact]
    public void Render_ShowsColumnsAndTasks()
    {
        var tasks = new List<TeamTask>
        {
            new() { Id = "t1", Subject = "design", Status = TeamTaskStatus.InProgress, Owner = "CodeAgent" },
            new() { Id = "t2", Subject = "research", Status = TeamTaskStatus.Pending, Assignee = "WebAgent" },
        };
        var text = KanbanCommand.Render("myteam", tasks);
        Assert.Contains("Kanban - myteam", text);
        Assert.Contains("IN PROGRESS", text);
        Assert.Contains("TODO", text);
        Assert.Contains("t1", text);
        Assert.Contains("@CodeAgent", text);
        Assert.Contains("->WebAgent", text);
    }

    [Fact]
    public void Render_OverLiveBoard_ReflectsCreateAndStatus()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mux-kanban-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var board = TaskBoard.Open("kb", dir);
        var a = board.Create("first", "");
        board.Create("second", "");
        board.TryClaim(a.Id, "Alice", out _);

        var text = KanbanCommand.Render(board.Team, board.Snapshot());
        Assert.Contains("IN PROGRESS (1)", text);
        Assert.Contains("TODO (1)", text);
        Assert.Contains("@Alice", text);
    }
}
