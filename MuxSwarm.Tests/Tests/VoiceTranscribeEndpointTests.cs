using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using MuxSwarm.Engine.Voice;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// <c>POST /api/voice/transcribe</c> (<see cref="VoiceTranscribeEndpoint"/>): status codes, the
/// whisper non-speech filter, and a success path through a mock whisper-cli script, invoking the
/// handler directly on a <see cref="DefaultHttpContext"/>.
/// </summary>
public sealed class VoiceTranscribeEndpointTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mux-vt-test-" + Guid.NewGuid().ToString("N"));
    private readonly Func<string> _cli = VoiceTranscribeEndpoint.CliPath;
    private readonly Func<string> _models = VoiceTranscribeEndpoint.ModelsDir;
    private readonly Func<string?> _ffmpeg = VoiceTranscribeEndpoint.FfmpegPath;

    public VoiceTranscribeEndpointTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, "models"));
        VoiceTranscribeEndpoint.ModelsDir = () => Path.Combine(_dir, "models");
        VoiceTranscribeEndpoint.CliPath = () => Path.Combine(_dir, "missing-whisper");
    }

    public void Dispose()
    {
        VoiceTranscribeEndpoint.CliPath = _cli;
        VoiceTranscribeEndpoint.ModelsDir = _models;
        VoiceTranscribeEndpoint.FfmpegPath = _ffmpeg;
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Theory]
    [InlineData("[BLANK_AUDIO]\n", "")]
    [InlineData(" (air whooshing)\r\n[MUSIC]\n", "")]
    [InlineData("", "")]
    [InlineData(" Hello from mux swarm.\n", "Hello from mux swarm.")]
    [InlineData("[MUSIC]\n Hello there.\n (wind)\n How are you?\n", "Hello there. How are you?")]
    [InlineData(" I said (quietly) hi\n", "I said (quietly) hi")]
    public void FilterNonSpeech_DropsWholeLineAnnotationsOnly(string raw, string expected)
        => Assert.Equal(expected, VoiceTranscribeEndpoint.FilterNonSpeech(raw));

    [Fact]
    public async Task UnsupportedContentType_Returns415()
    {
        var (status, json) = await Invoke("text/plain", Encoding.UTF8.GetBytes("hi"));
        Assert.Equal(415, status);
        Assert.Contains("Content-Type", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task NonWavWithoutFfmpeg_Returns415()
    {
        InstallMock("x");
        VoiceTranscribeEndpoint.FfmpegPath = () => null;
        var (status, json) = await Invoke("audio/webm", new byte[16]);
        Assert.Equal(415, status);
        Assert.Contains("ffmpeg", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task OversizeContentLength_Returns413()
    {
        var (status, _) = await Invoke("audio/wav", new byte[16], contentLength: VoiceTranscribeEndpoint.MaxBytes + 1);
        Assert.Equal(413, status);
    }

    [Fact]
    public async Task OversizeStreamedBody_Returns413()
    {
        InstallMock("x");
        var (status, _) = await Invoke("audio/wav", new byte[VoiceTranscribeEndpoint.MaxBytes + 10]);
        Assert.Equal(413, status);
    }

    [Fact]
    public async Task OverFiveMinutes_Returns413()
    {
        InstallMock("x");
        var (status, json) = await Invoke("audio/wav", Wav(seconds: 301));
        Assert.Equal(413, status);
        Assert.Contains("5 minutes", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task MissingAssets_Returns503()
    {
        var (status, json) = await Invoke("audio/wav", Wav(1));
        Assert.Equal(503, status);
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task ConcurrentRequest_Returns429()
    {
        InstallMock("x");
        Assert.True(await VoiceTranscribeEndpoint.Gate.WaitAsync(0));
        try
        {
            var (status, json) = await Invoke("audio/wav", Wav(1));
            Assert.Equal(429, status);
            Assert.Equal("busy", json.GetProperty("error").GetString());
        }
        finally { VoiceTranscribeEndpoint.Gate.Release(); }
    }

    [Fact]
    public async Task Success_ReturnsFilteredTextAndModelName()
    {
        InstallMock("[BLANK_AUDIO]", "Hello from mux swarm.", "(air whooshing)");
        int before = TempDirs();
        var (status, json) = await Invoke("audio/wav; codecs=1", Wav(1));
        Assert.Equal(200, status);
        Assert.Equal("Hello from mux swarm.", json.GetProperty("text").GetString());
        Assert.Equal("ggml-test.en", json.GetProperty("model").GetString());
        Assert.True(json.GetProperty("ms").GetInt64() >= 0);
        Assert.Equal(before, TempDirs()); // the per-request temp dir is deleted
    }

    [Fact]
    public async Task AnnotationOnlyTranscript_ReturnsEmptyText()
    {
        InstallMock("[BLANK_AUDIO]");
        var (status, json) = await Invoke("audio/wav", Wav(1));
        Assert.Equal(200, status);
        Assert.Equal("", json.GetProperty("text").GetString());
    }

    [Fact]
    public async Task DigitalSilence_ReturnsEmptyText_WithoutRunningWhisper()
    {
        InstallMock("you"); // what whisper hallucinates on silence
        var (status, json) = await Invoke("audio/wav", Wav(1, silent: true));
        Assert.Equal(200, status);
        Assert.Equal("", json.GetProperty("text").GetString());
        Assert.Equal("ggml-test.en", json.GetProperty("model").GetString());
    }

    [Fact]
    public async Task InvalidWav_Returns415()
    {
        InstallMock("x");
        var (status, _) = await Invoke("audio/wav", Encoding.ASCII.GetBytes("not a wav file at all"));
        Assert.Equal(415, status);
    }

    // ---------------------------------------------------------------- helpers

    private static int TempDirs() => Directory.GetDirectories(Path.GetTempPath(), "mux-transcribe-*").Length;

    /// <summary>Write a mock whisper-cli that prints the given lines, plus a dummy model file.</summary>
    private void InstallMock(params string[] lines)
    {
        File.WriteAllBytes(Path.Combine(_dir, "models", "ggml-test.en.bin"), new byte[4]);
        string path;
        if (OperatingSystem.IsWindows())
        {
            path = Path.Combine(_dir, "whisper-cli.cmd");
            var sb = new StringBuilder("@echo off\r\n");
            foreach (var l in lines) sb.Append("echo ").Append(l).Append("\r\n");
            File.WriteAllText(path, sb.ToString());
        }
        else
        {
            path = Path.Combine(_dir, "whisper-cli");
            var sb = new StringBuilder("#!/bin/sh\n");
            foreach (var l in lines) sb.Append("echo '").Append(l).Append("'\n");
            File.WriteAllText(path, sb.ToString());
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        VoiceTranscribeEndpoint.CliPath = () => path;
    }

    /// <summary>A 16 kHz mono 16-bit PCM WAV, <paramref name="seconds"/> long: a 440 Hz tone, or digital silence.</summary>
    private static byte[] Wav(int seconds, bool silent = false)
    {
        int data = 16000 * 2 * seconds;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(data);
        for (int i = 0; i < data / 2; i++)
            w.Write(silent ? (short)0 : (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / 16000.0)));
        w.Flush();
        return ms.ToArray();
    }

    private static async Task<(int Status, JsonElement Json)> Invoke(string contentType, byte[] body, long? contentLength = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/api/voice/transcribe";
        ctx.Request.ContentType = contentType;
        ctx.Request.ContentLength = contentLength ?? body.Length;
        ctx.Request.Body = new MemoryStream(body);
        var resp = new MemoryStream();
        ctx.Response.Body = resp;
        await VoiceTranscribeEndpoint.Handle(ctx);
        var text = Encoding.UTF8.GetString(resp.ToArray());
        return (ctx.Response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }
}
