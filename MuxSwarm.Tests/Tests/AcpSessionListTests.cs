using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MuxSwarm.Engine.Acp;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>session/list: rows Zed can show (absolute cwd, title, RFC 3339 updatedAt), cwd
/// filtering, paging, and swarm/unsaved folders skipped.</summary>
public class AcpSessionListTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "acp-list-" + Guid.NewGuid().ToString("N"));
    private static readonly string ProjA = Path.Combine(Path.GetTempPath(), "projA");
    private static readonly string ProjB = Path.Combine(Path.GetTempPath(), "projB");

    public AcpSessionListTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Session(string id, string firstUser, string? cwd, DateTime written)
    {
        string d = Path.Combine(_root, id);
        Directory.CreateDirectory(d);
        string file = Path.Combine(d, "agent_session.json");
        var json = JsonSerializer.Serialize(new
        {
            stateBag = new { InMemoryChatHistoryProvider = new { messages = new object[]
            {
                new Dictionary<string, object> { ["role"] = "user", ["contents"] = new object[] { new Dictionary<string, object> { ["$type"] = "text", ["text"] = firstUser } } }
            } } }
        });
        File.WriteAllText(file, json);
        File.SetLastWriteTimeUtc(file, written);
        if (cwd is not null) File.WriteAllText(Path.Combine(d, AcpServer.CwdFileName), cwd);
        return d;
    }

    private static JsonElement Reparse(object o) => JsonDocument.Parse(AcpProtocol.Serialize(o)).RootElement;

    [Fact]
    public void Lists_NewestFirst_WithCwdTitleAndUpdatedAt_FilteredByCwd()
    {
        Session("2026-10-01_10-00-00", "fix the   login\nbug", ProjA, new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc));
        Session("2026-10-02_10-00-00", "other project", ProjB, new DateTime(2026, 10, 2, 10, 0, 0, DateTimeKind.Utc));
        Session("2026-10-03_10-00-00", "started in the TUI", null, new DateTime(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc));
        Directory.CreateDirectory(Path.Combine(_root, "2026-10-04_10-00-00"));   // checkpoint, no file yet
        var swarm = Path.Combine(_root, "2026-10-05_10-00-00"); Directory.CreateDirectory(swarm);
        foreach (var n in new[] { "orchestrator_session.json", "a_session.json", "b_session.json" }) File.WriteAllText(Path.Combine(swarm, n), "{}");

        var all = Reparse(AcpServer.ListSessions(_root, null, null, ProjA)).GetProperty("sessions");
        Assert.Equal(new[] { "2026-10-03_10-00-00", "2026-10-02_10-00-00", "2026-10-01_10-00-00" },
            all.EnumerateArray().Select(s => s.GetProperty("sessionId").GetString()).ToArray());
        var first = all[2];
        Assert.Equal(ProjA, first.GetProperty("cwd").GetString());
        Assert.Equal("fix the login bug", first.GetProperty("title").GetString());
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), DateTimeOffset.Parse(first.GetProperty("updatedAt").GetString()!));
        Assert.Equal(ProjA, all[0].GetProperty("cwd").GetString());   // no recorded cwd -> fallback
        Assert.False(Reparse(AcpServer.ListSessions(_root, null, null, ProjA)).TryGetProperty("nextCursor", out _));

        var onlyB = Reparse(AcpServer.ListSessions(_root, ProjB, null, ProjA)).GetProperty("sessions");
        Assert.Equal(new[] { "2026-10-03_10-00-00", "2026-10-02_10-00-00" },
            onlyB.EnumerateArray().Select(s => s.GetProperty("sessionId").GetString()).ToArray());
        Assert.Equal(ProjB, onlyB[0].GetProperty("cwd").GetString());   // unrecorded row reports the filter cwd
    }

    [Fact]
    public void Pages_With_Opaque_Cursor()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 55; i++) Session($"s{i:D2}", "q" + i, ProjA, t0.AddMinutes(i));
        var p1 = Reparse(AcpServer.ListSessions(_root, ProjA, null, ProjA));
        Assert.Equal(50, p1.GetProperty("sessions").GetArrayLength());
        Assert.Equal("s54", p1.GetProperty("sessions")[0].GetProperty("sessionId").GetString());
        var p2 = Reparse(AcpServer.ListSessions(_root, ProjA, p1.GetProperty("nextCursor").GetString(), ProjA));
        Assert.Equal(5, p2.GetProperty("sessions").GetArrayLength());
        Assert.False(p2.TryGetProperty("nextCursor", out _));
    }

    [Fact]
    public void Empty_Or_Missing_Dir_Is_Empty_Array()
    {
        var r = Reparse(AcpServer.ListSessions(Path.Combine(_root, "nope"), null, null, ProjA));
        Assert.Equal(0, r.GetProperty("sessions").GetArrayLength());
    }

    [Fact]
    public void Initialize_Advertises_List()
    {
        var caps = Reparse(AcpProtocol.InitializeResult("t", loadSession: true)).GetProperty("agentCapabilities");
        Assert.True(caps.GetProperty("sessionCapabilities").TryGetProperty("list", out _));
        Assert.True(caps.GetProperty("loadSession").GetBoolean());
    }

    [Fact]
    public void Title_Collapses_Whitespace_And_Truncates()
    {
        Assert.Equal("a b c", AcpServer.Title("  a\n\tb   c "));
        Assert.Equal(80, AcpServer.Title(new string('x', 200)).Length);
    }
}
