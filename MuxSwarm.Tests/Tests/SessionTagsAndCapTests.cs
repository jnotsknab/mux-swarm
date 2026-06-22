using System;
using System.IO;
using System.Linq;
using MuxSwarm.Utils;
using Xunit;

namespace MuxSwarm.Tests.Tests;

public class SessionTagsAndCapTests
{
    private static string TempSessionDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "muxtag-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Append_ThenRead_RoundTrips()
    {
        var dir = TempSessionDir();
        try
        {
            Assert.True(SessionTags.Append(dir, "refactor the footer"));
            Assert.True(SessionTags.Append(dir, "v0.11 work"));
            var tags = SessionTags.Read(dir);
            Assert.Equal(new[] { "refactor the footer", "v0.11 work" }, tags);
            Assert.True(SessionTags.HasTags(dir));
            Assert.Equal("refactor the footer, v0.11 work", SessionTags.TagLabel(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void TagSidecar_IsNotJson_DoesNotChangeJsonCount()
    {
        // The single-agent-vs-swarm resume detector counts *.json files. The tag sidecar must
        // be invisible to it.
        var dir = TempSessionDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "agent_session.json"), "{}");
            int before = Directory.GetFiles(dir, "*.json").Length;
            SessionTags.Append(dir, "some tag");
            int after = Directory.GetFiles(dir, "*.json").Length;
            Assert.Equal(before, after);
            Assert.Equal(SessionTags.TagFileName, "tags.muxtag");
            Assert.False(SessionTags.TagFileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Append_NormalizesMultilineToSingleLine()
    {
        var dir = TempSessionDir();
        try
        {
            SessionTags.Append(dir, "line one\nline two");
            var tags = SessionTags.Read(dir);
            Assert.Single(tags);
            Assert.DoesNotContain("\n", tags[0]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Read_EmptyWhenNoSidecar()
    {
        var dir = TempSessionDir();
        try
        {
            Assert.Empty(SessionTags.Read(dir));
            Assert.False(SessionTags.HasTags(dir));
            Assert.Null(SessionTags.TagLabel(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(0, "force", 99999, "none")]   // limit 0 = off
    [InlineData(100, "off", 99999, "none")]   // mode off
    [InlineData(100, "warn", 50, "none")]     // under limit
    [InlineData(100, "warn", 150, "warn")]    // over, warn
    [InlineData(100, "force", 150, "force")]  // over, force
    [InlineData(100, "FORCE", 150, "force")]  // case-insensitive
    [InlineData(100, null, 150, "none")]      // null mode -> off
    public void ClassifyAction_MatchesMatrix(int limit, string? mode, int len, string expected)
    {
        Assert.Equal(expected, ContextCap.ClassifyAction(len, limit, mode));
    }
}
