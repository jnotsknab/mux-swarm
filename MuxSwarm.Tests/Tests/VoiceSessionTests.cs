using System;
using System.Linq;
using MuxSwarm.Utils.Voice;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// /voice engine coverage for the pure logic: energy-VAD math, the manual-mode "send" keyword,
/// whisper transcript cleaning, the pinned asset table, and the archive-extraction file filter.
/// The mic/whisper subprocess paths need real audio hardware and are validated by live probe.
/// </summary>
public class VoiceSessionTests
{
    // --- FrameRms -----------------------------------------------------------

    [Fact]
    public void FrameRms_Silence_IsZero()
    {
        var pcm = new byte[3200]; // all zero samples
        Assert.Equal(0, VoiceSession.FrameRms(pcm), 5);
    }

    [Fact]
    public void FrameRms_FullScale_IsNearOne()
    {
        var pcm = new byte[3200];
        for (int i = 0; i < pcm.Length; i += 2) { pcm[i] = 0xFF; pcm[i + 1] = 0x7F; } // +32767
        Assert.True(VoiceSession.FrameRms(pcm) > 0.99);
    }

    [Fact]
    public void FrameRms_QuietSpeechLevel_ClearsDefaultThreshold()
    {
        // ~ -30 dBFS sine-ish level (amplitude ~1000/32768) should read well above the default gate.
        var pcm = new byte[3200];
        for (int i = 0; i < pcm.Length; i += 2)
        {
            short s = (short)(Math.Sin(i * 0.1) * 1000);
            pcm[i] = (byte)(s & 0xFF); pcm[i + 1] = (byte)((s >> 8) & 0xFF);
        }
        Assert.True(VoiceSession.FrameRms(pcm) > VoiceSession.VadThreshold);
    }

    // --- Sensitivity / VadThreshold mapping (/voice vol) ---------------------

    [Fact]
    public void Sensitivity_Default_IsMidScale()
        => Assert.Equal(5, VoiceSession.Sensitivity);

    [Fact]
    public void Sensitivity_Clamps_To1Through10()
    {
        var prev = VoiceSession.Sensitivity;
        try
        {
            VoiceSession.Sensitivity = 0;   Assert.Equal(1, VoiceSession.Sensitivity);
            VoiceSession.Sensitivity = 99;  Assert.Equal(10, VoiceSession.Sensitivity);
            VoiceSession.Sensitivity = -5;  Assert.Equal(1, VoiceSession.Sensitivity);
        }
        finally { VoiceSession.Sensitivity = prev; }
    }

    [Fact]
    public void VadThreshold_IsMonotonicDecreasing_AsSensitivityRises()
    {
        var prev = VoiceSession.Sensitivity;
        try
        {
            double last = double.MaxValue;
            for (int lvl = 1; lvl <= 10; lvl++)
            {
                VoiceSession.Sensitivity = lvl;
                Assert.True(VoiceSession.VadThreshold < last,
                    $"threshold must drop as sensitivity rises (lvl {lvl})");
                last = VoiceSession.VadThreshold;
            }
        }
        finally { VoiceSession.Sensitivity = prev; }
    }

    [Fact]
    public void VadThreshold_EndpointsMatchDesign()
    {
        var prev = VoiceSession.Sensitivity;
        try
        {
            VoiceSession.Sensitivity = 1;
            Assert.Equal(0.040, VoiceSession.VadThreshold, 3);
            VoiceSession.Sensitivity = 10;
            Assert.Equal(0.004, VoiceSession.VadThreshold, 3);
        }
        finally { VoiceSession.Sensitivity = prev; }
    }

    [Fact]
    public void FrameRms_EmptyOrTiny_IsZero()
    {
        Assert.Equal(0, VoiceSession.FrameRms(Array.Empty<byte>()));
        Assert.Equal(0, VoiceSession.FrameRms(new byte[1]));
    }

    // --- IsSendKeyword ------------------------------------------------------

    [Theory]
    [InlineData("send")]
    [InlineData("Send")]
    [InlineData("SEND")]
    [InlineData(" send ")]
    [InlineData("send.")]
    [InlineData("Send!")]
    [InlineData("send?")]
    [InlineData("Send,")]
    public void IsSendKeyword_MatchesBareSendWithPunctuation(string t)
        => Assert.True(VoiceSession.IsSendKeyword(t));

    [Theory]
    [InlineData("send it")]
    [InlineData("please send")]
    [InlineData("sending")]
    [InlineData("resend")]
    [InlineData("")]
    [InlineData("fix the bug then send")]
    public void IsSendKeyword_RejectsAnythingElse(string t)
        => Assert.False(VoiceSession.IsSendKeyword(t));

    // --- CleanTranscript ----------------------------------------------------

    [Fact]
    public void CleanTranscript_StripsNonSpeechMarkers()
    {
        Assert.Equal("hello world", VoiceSession.CleanTranscript(" [BLANK_AUDIO] hello (wind) world [Music] "));
    }

    [Fact]
    public void CleanTranscript_CollapsesWhitespace()
    {
        Assert.Equal("a b c", VoiceSession.CleanTranscript("  a\n  b\t c "));
    }

    [Fact]
    public void CleanTranscript_NullOrMarkersOnly_IsEmpty()
    {
        Assert.Equal("", VoiceSession.CleanTranscript(null!));
        Assert.Equal("", VoiceSession.CleanTranscript("[BLANK_AUDIO]"));
    }

    // --- state surface ------------------------------------------------------

    [Fact]
    public void VoiceSession_DefaultState_IsOffAndInactive()
    {
        Assert.Equal(VoiceState.Off, VoiceSession.State);
        Assert.False(VoiceSession.IsActive);
    }

    [Fact]
    public void Stop_WhenOff_IsGracefulNoop()
    {
        var msg = VoiceSession.Stop();
        Assert.Contains("not on", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(VoiceState.Off, VoiceSession.State);
    }
}

/// <summary>Pinned whisper.cpp asset-table + extraction-filter invariants.</summary>
public class WhisperAssetsTests
{
    [Fact]
    public void PinnedArtifacts_CoverWinAndLinux()
    {
        var rids = WhisperAssets.Artifacts.Select(a => a.Rid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("win-x64", rids);
        Assert.Contains("linux-x64", rids);
        Assert.Contains("linux-arm64", rids);
    }

    [Fact]
    public void PinnedArtifacts_HaveShaAndVersionedUrl()
    {
        foreach (var a in WhisperAssets.Artifacts)
        {
            Assert.Equal(64, a.Sha256.Length);
            Assert.Contains(WhisperAssets.Version, a.Url);
            Assert.StartsWith("https://github.com/ggml-org/whisper.cpp/releases/download/", a.Url);
        }
    }

    [Fact]
    public void ForRid_UnknownRid_IsNull()
        => Assert.Null(WhisperAssets.ForRid("osx-arm64")); // no prebuilt mac CLI in v1

    [Theory]
    [InlineData("whisper-cli.exe", true)]
    [InlineData("whisper-cli", true)]
    [InlineData("whisper.dll", true)]
    [InlineData("ggml.dll", true)]
    [InlineData("ggml-cpu-haswell.dll", true)]
    [InlineData("libwhisper.so.1", true)]
    [InlineData("libggml.so", true)]
    [InlineData("SDL2.dll", false)]              // demo dependency - not wanted
    [InlineData("whisper-talk-llama.exe", false)] // demo binary
    [InlineData("main.exe", false)]
    [InlineData("bench.exe", false)]
    [InlineData("parakeet.dll", false)]
    public void WantedBinFile_FiltersToCliAndItsLibraries(string name, bool wanted)
        => Assert.Equal(wanted, WhisperAssets.WantedBinFile(name));

    [Fact]
    public void ModelPin_IsTheQuantizedBaseEn()
    {
        Assert.Equal("ggml-base.en-q5_1.bin", WhisperAssets.ModelFileName);
        Assert.EndsWith(WhisperAssets.ModelFileName, WhisperAssets.ModelPath);
    }
}
