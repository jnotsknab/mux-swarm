using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>Web UI voice input: server transcription failures and empty results are shown, never only logged.</summary>
public class WebVoiceFeedbackTests
{
    private static string VoiceSource()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "MuxSwarm.csproj"))) d = d.Parent;
        var html = File.ReadAllText(Path.Combine(d!.FullName, "Runtime", "mux-web-app", "index.html"));
        int a = html.IndexOf("async function stopCapture()", StringComparison.Ordinal);
        int b = html.IndexOf("const statusDot = $('statusDot');", a, StringComparison.Ordinal);
        Assert.True(a > 0 && b > a, "voice capture block not found");
        return html[a..b];
    }

    [Fact]
    public void ServerTranscribeFailure_ShowsTheServersReason()
    {
        var src = VoiceSource();
        // 503 (whisper not installed) / 415 (needs ffmpeg) carry an { error } body: it must reach the user.
        Assert.Contains("(await r.json()).error", src);
        Assert.Contains("showToast(why, 'error')", src);
        Assert.Contains("showToast('Voice: no speech detected', 'error')", src);
        Assert.DoesNotContain("console.log('No speech detected')", src);
    }
}
