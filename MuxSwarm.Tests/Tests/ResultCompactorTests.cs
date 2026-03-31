using MuxSwarm.Utils;

namespace MuxSwarm.Tests.Tests;

public class ResultCompactorTests
{
    // ── CompactAsync: short results pass through ───────────────────────

    [Fact]
    public async Task CompactAsync_ShortResult_ReturnsUnchanged()
    {
        var shortText = "Done. File saved.";
        var result = await ResultCompactor.CompactAsync(shortText, charBudget: 800);
        Assert.Equal(shortText, result);
    }

    // ── CompactAsync: structured fields take priority ──────────────────

    [Fact]
    public async Task CompactAsync_WithCompletionSummary_ReturnsStructured()
    {
        var result = await ResultCompactor.CompactAsync(
            rawResult: "This is a very long result that should not be used because structured fields are available...",
            completionStatus: "success",
            completionSummary: "Created 3 files and ran tests.",
            completionArtifacts: "main.cs, tests.cs, config.json",
            charBudget: 800
        );

        Assert.Contains("success", result);
        Assert.Contains("Created 3 files and ran tests.", result);
        Assert.Contains("main.cs", result);
    }

    [Fact]
    public async Task CompactAsync_WithCompletionSummary_NoArtifacts()
    {
        var result = await ResultCompactor.CompactAsync(
            rawResult: "long ignored text...",
            completionStatus: "partial",
            completionSummary: "2 of 3 tasks completed.",
            charBudget: 800
        );

        Assert.Contains("partial", result);
        Assert.Contains("2 of 3 tasks completed.", result);
        Assert.DoesNotContain("artifacts", result);
    }

    // ── CompactAsync: extractive compaction for long text ──────────────

    [Fact]
    public async Task CompactAsync_LongResult_NoLlm_ExtractsLines()
    {
        var longText = string.Join("\n",
            "I will now proceed to help you with this task.",
            "The task was to build something great.",
            "Error: Connection refused on port 8080.",
            "Warning: Disk usage at 90% capacity.",
            "Let me fix that right away.",
            "Success: All 15 files created successfully.",
            "The goal was to create files for the project.",
            "Created /home/user/project/main.cs",
            "Created /home/user/project/config.json",
            "I hope this helps you out!",
            "Okay, I'm done now."
        );

        var result = await ResultCompactor.CompactAsync(longText, charBudget: 200, chatClient: null);

        Assert.True(result.Length <= 300, $"Result too long: {result.Length} chars");
        Assert.Contains("Error", result);
        Assert.Contains("Success", result);
    }

    [Fact]
    public async Task CompactAsync_VeryLongResult_FitsWithinBudget()
    {
        var lines = Enumerable.Range(0, 100)
            .Select(i => $"Line {i}: This is some content that takes up space in the result buffer.")
            .ToArray();
        var longText = string.Join("\n", lines);

        var result = await ResultCompactor.CompactAsync(longText, charBudget: 500, chatClient: null);
        Assert.True(result.Length <= 600, $"Result too long: {result.Length} chars");
    }

    // ── CompactAsync: empty/null inputs ────────────────────────────────

    [Fact]
    public async Task CompactAsync_EmptyResult_ReturnsEmpty()
    {
        var result = await ResultCompactor.CompactAsync("", charBudget: 800);
        Assert.Equal("", result);
    }

    [Fact]
    public async Task CompactAsync_NullCompletionFields_FallsBackToRawResult()
    {
        var raw = "Short result without structured fields.";
        var result = await ResultCompactor.CompactAsync(
            raw, completionStatus: null, completionSummary: null, completionArtifacts: null, charBudget: 800);
        Assert.Equal(raw, result);
    }
}
