using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>Phone composer (both styles): two planned rows, pickers keep their labels, popups stay on screen.</summary>
public class WebMobileComposerTests
{
    private static string Html()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "MuxSwarm.csproj"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "Runtime", "mux-web-app", "index.html"));
    }

    [Fact]
    public void Phones_GetTwoRows_PickersFirst_SendLast()
    {
        var html = Html();
        // Not scoped to one style: Classic crushed the pickers to 35-50px, Broadsheet wrapped at random.
        Assert.Contains("html .input-area .btn-group > :is(.session-btn, .model-btn) { order: 1; flex: 1 1 calc(50% - 3px);", html);
        Assert.Contains("html .input-area .send-btn { order: 3; margin-left: auto; height: 36px; }", html);
        Assert.Contains("html .input-area .btn-group > * { order: 2; flex: 0 0 auto; }", html);
    }

    [Fact]
    public void Phones_PopupsAnchorToTheComposer_NotOffScreen()
    {
        var html = Html();
        Assert.Contains("html .input-area .btn-group > span:has(> .mux-cchip) { position: static !important; }", html);
        Assert.Contains("html .input-area .mux-cpop { left: 0; right: 0;", html);
        Assert.Contains("#muxGauge > span:last-child { display: none; }", html);   // ring only on phones; % is in its menu
    }
}
