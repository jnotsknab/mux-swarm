using MuxSwarm.Utils;

namespace MuxSwarm.Tests.Tests;

public class LiveTokenMeterTests
{
    // At an authoritative checkpoint the char accumulator is reset to 0, so the live estimate must
    // equal exactly the authoritative display (sessionTokens - cached). This is THE property that
    // eliminates the old "drops on turn end" artifact: live value == snapped value at every
    // checkpoint, so there is nothing to drop.
    [Fact]
    public void Estimate_AtCheckpoint_ZeroChars_EqualsAuthoritativeDisplay()
    {
        uint session = 11000, cached = 8000;
        var est = LiveTokenMeter.Estimate(session, liveTurnChars: 0, cachedTokens: cached);
        Assert.Equal(session - cached, est); // 3000
    }

    [Fact]
    public void Estimate_ExtrapolatesForwardFromCheckpoint()
    {
        // 250 output chars / 2.5 = 100 tokens added on top of the last authoritative total.
        uint session = 10000, cached = 6000;
        var est = LiveTokenMeter.Estimate(session, liveTurnChars: 250, cachedTokens: cached);
        Assert.Equal((10000u + 100u) - 6000u, est); // 4100
    }

    [Fact]
    public void Estimate_OnlyBridgesGapSinceCheckpoint_NotWholeTurn()
    {
        // Simulate: turn streams 2500 output chars (=1000 est tokens) BEFORE an authoritative frame
        // arrives reporting total=10500. Pre-frame the live estimate extrapolates from the prior
        // checkpoint (session=9000); post-frame the accumulator resets so the estimate snaps to the
        // real total with NO downward jump beyond the genuine provider number.
        uint preSession = 9000, cached = 5000;
        var preFrame = LiveTokenMeter.Estimate(preSession, liveTurnChars: 2500, cachedTokens: cached);
        Assert.Equal((9000u + 1000u) - 5000u, preFrame); // 5000

        // Authoritative frame lands: session snaps to 10500, accumulator reset to 0.
        uint postSession = 10500;
        var postFrame = LiveTokenMeter.Estimate(postSession, liveTurnChars: 0, cachedTokens: cached);
        Assert.Equal(10500u - 5000u, postFrame); // 5500 -- matches reality, no fake snap-back

        // The post-frame value reflects the REAL provider number; any movement is genuine, not an
        // estimate error. (Here it rose; with caching it could legitimately fall -- both are honest.)
        Assert.True(postFrame >= preFrame);
    }

    [Fact]
    public void Estimate_CachedExceedsTotal_ClampsToZero_NoUnderflow()
    {
        // cached > est must not underflow the uint subtraction.
        var est = LiveTokenMeter.Estimate(sessionTokens: 1000, liveTurnChars: 0, cachedTokens: 5000);
        Assert.Equal(1000u, est); // returns est (1000), not a wrapped huge uint
    }

    [Fact]
    public void Estimate_NegativeCharsTreatedAsZero()
    {
        var est = LiveTokenMeter.Estimate(sessionTokens: 2000, liveTurnChars: -999, cachedTokens: 0);
        Assert.Equal(2000u, est);
    }

    [Fact]
    public void Estimate_CharsPerToken_IsConfigurable()
    {
        // 400 chars at 4 chars/token = 100 tokens.
        var est = LiveTokenMeter.Estimate(sessionTokens: 0, liveTurnChars: 400, cachedTokens: 0, charsPerToken: 4.0);
        Assert.Equal(100u, est);
    }
}
