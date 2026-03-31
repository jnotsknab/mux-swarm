using System.Text.Json;
using MuxSwarm.Utils;

namespace MuxSwarm.Tests.Tests;

public class CommonTests
{
    // ── LooksLikeEnvVarName ────────────────────────────────────────────

    [Theory]
    [InlineData("HOME")]
    [InlineData("PATH")]
    [InlineData("MY_VAR_123")]
    [InlineData("_UNDERSCORE_START")]
    [InlineData("A")]
    [InlineData("API_KEY")]
    public void LooksLikeEnvVarName_ValidNames_ReturnsTrue(string name)
    {
        Assert.True(Common.LooksLikeEnvVarName(name));
    }

    [Theory]
    [InlineData("home")]
    [InlineData("my-var")]
    [InlineData("123ABC")]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("has-dash")]
    [InlineData("dot.value")]
    public void LooksLikeEnvVarName_InvalidNames_ReturnsFalse(string name)
    {
        Assert.False(Common.LooksLikeEnvVarName(name));
    }

    // ── ReadGoalValue ──────────────────────────────────────────────────

    [Fact]
    public async Task ReadGoalValue_FileExists_ReturnsFileContents()
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "goal.txt");
        var dir = Path.GetDirectoryName(tmpFile)!;
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(tmpFile, "build the thing");
            var result = Common.ReadGoalValue(tmpFile);
            Assert.Equal("build the thing", result);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ReadGoalValue_FileDoesNotExist_ReturnsInputString()
    {
        var result = Common.ReadGoalValue("just do it directly");
        Assert.Equal("just do it directly", result);
    }

    // ── EstimateTokenCount (JsonElement) ───────────────────────────────

    [Fact]
    public void EstimateTokenCount_JsonElement_ReturnsPositiveInt()
    {
        var json = JsonDocument.Parse("{\"key\": \"value\"}");
        var count = Common.EstimateTokenCount(json.RootElement);
        Assert.True(count > 0);
    }

    // ── EstimateTokenCount (ChatMessage) ───────────────────────────────

    [Fact]
    public void EstimateTokenCount_EmptyHistory_ReturnsZero()
    {
        var count = Common.EstimateTokenCount(Array.Empty<Microsoft.Extensions.AI.ChatMessage>());
        Assert.Equal(0, count);
    }

    [Fact]
    public void EstimateTokenCount_SingleMessage_ReturnsPositiveInt()
    {
        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.User, "Hello world, this is a test message.")
        };
        var count = Common.EstimateTokenCount(messages);
        Assert.True(count > 0);
    }

    // ── GetOsFriendlyName ──────────────────────────────────────────────

    [Fact]
    public void GetOsFriendlyName_ReturnsKnownPlatform()
    {
        var name = Common.GetOsFriendlyName();
        Assert.NotEmpty(name);
        Assert.Contains(name, new[] { "Windows", "macOS", "Linux" });
    }

    // ── ResolveDirCaseInsensitive ──────────────────────────────────────

    [Fact]
    public async Task ResolveDirCaseInsensitive_ExistingDir_ReturnsPath()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(tmpDir);
            var result = Common.ResolveDirCaseInsensitive(Path.GetTempPath(), Path.GetFileName(tmpDir));
            Assert.NotNull(result);
            Assert.Equal(tmpDir, result, ignoreCase: true);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [Fact]
    public void ResolveDirCaseInsensitive_NonExistentParent_ReturnsNull()
    {
        var result = Common.ResolveDirCaseInsensitive("/nonexistent/path/12345", "subdir");
        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveDirCaseInsensitive_NoMatch_ReturnsNull()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(tmpDir);
            var result = Common.ResolveDirCaseInsensitive(Path.GetTempPath(), "nonexistent_dir_xyz");
            Assert.Null(result);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    // ── GetFirstUserMessage ────────────────────────────────────────────

    [Fact]
    public async Task GetFirstUserMessage_ValidSession_ReturnsMessage()
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "session.json");
        var dir = Path.GetDirectoryName(tmpFile)!;
        try
        {
            Directory.CreateDirectory(dir);

            var sessionJson = """
            {
                "chatHistoryProviderState": {
                    "messages": [
                        {
                            "role": "user",
                            "contents": [{"$type": "text", "text": "Build me a REST API"}]
                        }
                    ]
                }
            }
            """;
            await File.WriteAllTextAsync(tmpFile, sessionJson);

            var result = Common.GetFirstUserMessage(tmpFile);
            Assert.Equal("Build me a REST API", result);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task GetFirstUserMessage_NoUserMessages_ReturnsNoPreview()
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "session.json");
        var dir = Path.GetDirectoryName(tmpFile)!;
        try
        {
            Directory.CreateDirectory(dir);

            var sessionJson = """
            {
                "chatHistoryProviderState": {
                    "messages": [
                        {
                            "role": "assistant",
                            "contents": [{"$type": "text", "text": "I will help you."}]
                        }
                    ]
                }
            }
            """;
            await File.WriteAllTextAsync(tmpFile, sessionJson);

            var result = Common.GetFirstUserMessage(tmpFile);
            Assert.Equal("No preview", result);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void GetFirstUserMessage_InvalidJson_ReturnsNoPreview()
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "bad.json");
        var dir = Path.GetDirectoryName(tmpFile)!;
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(tmpFile, "not json at all");

            var result = Common.GetFirstUserMessage(tmpFile);
            Assert.Equal("No preview", result);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task GetFirstUserMessage_LongMessage_TruncatesToMaxLength()
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "session.json");
        var dir = Path.GetDirectoryName(tmpFile)!;
        try
        {
            Directory.CreateDirectory(dir);

            var longText = new string('A', 100);
            var sessionJson = $$"""
            {
                "chatHistoryProviderState": {
                    "messages": [
                        {
                            "role": "user",
                            "contents": [{"$type": "text", "text": "{{longText}}"}]
                        }
                    ]
                }
            }
            """;
            await File.WriteAllTextAsync(tmpFile, sessionJson);

            var result = Common.GetFirstUserMessage(tmpFile, maxLength: 40);
            Assert.EndsWith("...", result);
            Assert.True(result.Length <= 43); // 40 chars + "..."
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
