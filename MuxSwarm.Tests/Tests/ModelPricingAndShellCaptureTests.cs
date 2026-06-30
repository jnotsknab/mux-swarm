using System.Threading;
using System.Threading.Tasks;
using MuxSwarm.Utils;
using Xunit;

namespace MuxSwarm.Tests.Tests;

public class ModelPricingTests
{
    [Theory]
    [InlineData("claude-opus-4-6", 15.00, 75.00)]
    [InlineData("claude-sonnet-4-5", 3.00, 15.00)]
    [InlineData("gpt-5-codex", 1.25, 10.00)]
    [InlineData("gemini-flash-2", 0.075, 0.30)]
    public void Lookup_MatchesBySubstring(string model, double inP, double outP)
    {
        var price = ModelPricing.Lookup(model);
        Assert.NotNull(price);
        Assert.Equal(inP, price!.Value.InputPer1M, 3);
        Assert.Equal(outP, price.Value.OutputPer1M, 3);
    }

    [Fact]
    public void Lookup_UnknownModel_ReturnsNull()
    {
        Assert.Null(ModelPricing.Lookup("some-random-local-model-xyz"));
        Assert.Null(ModelPricing.Lookup(null));
        Assert.Null(ModelPricing.Lookup(""));
    }

    [Fact]
    public void Estimate_ComputesPerMillion()
    {
        // claude-opus: 15/1M in, 75/1M out. 1M in + 1M out = 90.
        var usd = ModelPricing.Estimate("claude-opus-4-6", 1_000_000, 1_000_000);
        Assert.NotNull(usd);
        Assert.Equal(90.0, usd!.Value, 3);
    }

    [Fact]
    public void Estimate_UnknownModel_ReturnsNull()
        => Assert.Null(ModelPricing.Estimate("mystery-model", 1000, 1000));

    [Fact]
    public void Overrides_WinOverBuiltInAndLongestMatches()
    {
        ModelPricing.Overrides["mystery-model"] = new ModelPricing.Price(1.0, 2.0);
        try
        {
            var p = ModelPricing.Lookup("acme-mystery-model-v2");
            Assert.NotNull(p);
            Assert.Equal(1.0, p!.Value.InputPer1M, 3);
        }
        finally { ModelPricing.Overrides.Clear(); }
    }
}

public class ShellCaptureTests
{
    [Fact]
    public async Task RunAsync_CapturesStdout()
    {
        var cmd = System.OperatingSystem.IsWindows() ? "echo hello-mux" : "echo hello-mux";
        var res = await ShellCapture.RunAsync(cmd, System.IO.Path.GetTempPath(), 15, 10_000, CancellationToken.None);
        Assert.Contains("hello-mux", res.Stdout);
        Assert.False(res.TimedOut);
    }

    [Fact]
    public async Task RunAsync_CapsOutput()
    {
        // emit more than the cap; assert truncation marker present
        var cmd = System.OperatingSystem.IsWindows()
            ? "for /L %i in (1,1,200) do @echo 0123456789"
            : "for i in $(seq 1 200); do echo 0123456789; done";
        var res = await ShellCapture.RunAsync(cmd, System.IO.Path.GetTempPath(), 15, 200, CancellationToken.None);
        Assert.True(res.Stdout.Length <= 260);
        Assert.Contains("truncated", res.Stdout);
    }
}
