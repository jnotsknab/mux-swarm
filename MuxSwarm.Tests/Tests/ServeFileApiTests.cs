using MuxSwarm.Engine;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>/api/file resolution and kinds.</summary>
public class ServeFileApiTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "mux-fileapi-" + Guid.NewGuid().ToString("N"));
    private readonly string _sandbox, _allowed, _outside;

    public ServeFileApiTests()
    {
        _sandbox = Path.Combine(_base, "sandbox");
        _allowed = Path.Combine(_base, "project");
        _outside = Path.Combine(_base, "outside");
        foreach (var d in new[] { _sandbox, Path.Combine(_sandbox, "out"), _allowed, _outside }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(_sandbox, "out", "chart.png"), "x");
        File.WriteAllText(Path.Combine(_allowed, "README.md"), "# hi");
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "s");
    }

    public void Dispose() { try { Directory.Delete(_base, true); } catch { } }

    private IReadOnlyList<string> Roots => [_sandbox, _allowed];

    [Fact]
    public void RelativePath_ResolvesAgainstTheSandbox() =>
        Assert.Equal(Path.Combine(_sandbox, "out", "chart.png"), ServeMode.ResolveServableFile("out/chart.png", Roots));

    [Fact]
    public void AbsolutePath_InAnAllowedRoot_Resolves()
    {
        var p = Path.Combine(_allowed, "README.md");
        Assert.Equal(p, ServeMode.ResolveServableFile(p, Roots));
        Assert.Equal(p, ServeMode.ResolveServableFile(new Uri(p).AbsoluteUri, Roots)); // file:// form
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("out/missing.png")]
    [InlineData("../outside/secret.txt")]
    public void MissingOrOutside_IsNull(string path) => Assert.Null(ServeMode.ResolveServableFile(path, Roots));

    [Fact]
    public void AbsolutePath_OutsideEveryRoot_IsNull() =>
        Assert.Null(ServeMode.ResolveServableFile(Path.Combine(_outside, "secret.txt"), Roots));

    [Fact]
    public void ExcludedFolder_IsNull_EvenInsideAnAllowedRoot() // config folder when serve.configExposed is off
    {
        var cfgDir = Path.Combine(_allowed, "Configs");
        Directory.CreateDirectory(cfgDir);
        var cfg = Path.Combine(cfgDir, "Config.json");
        File.WriteAllText(cfg, "{}");
        Assert.Equal(cfg, ServeMode.ResolveServableFile(cfg, Roots));
        Assert.Null(ServeMode.ResolveServableFile(cfg, Roots, [cfgDir]));
        Assert.NotNull(ServeMode.ResolveServableFile(Path.Combine(_allowed, "README.md"), Roots, [cfgDir]));
    }

    [Fact]
    public void Directory_IsNull() => Assert.Null(ServeMode.ResolveServableFile(_allowed, Roots));

    [Theory]
    [InlineData("a.png", "image")]
    [InlineData("a.SVG", "image")]
    [InlineData("a.mp4", "video")]
    [InlineData("a.wav", "audio")]
    [InlineData("a.pdf", "pdf")]
    [InlineData("a.md", "markdown")]
    [InlineData("a.html", "html")]
    [InlineData("a.json", "json")]
    [InlineData("a.csv", "table")]
    [InlineData("a.cs", "text")]
    [InlineData("a.txt", "text")]
    [InlineData("a.exe", "binary")]
    public void FileKind_FromExtension(string name, string kind) => Assert.Equal(kind, ServeMode.FileKind(name));

    // Files come from roots that hold content the user did not write, on the web UI's origin: an HTML/SVG file
    // opened directly must not run script with access to the page's token.
    [Theory]
    [InlineData("page.html", false, "sandbox")]
    [InlineData("page.HTM", false, "sandbox")]
    [InlineData("logo.svg", false, "sandbox")]
    [InlineData("feed.xml", false, "sandbox")]
    [InlineData("page.html", true, null)]      // attachment: the browser saves it, nothing runs
    [InlineData("chart.png", false, null)]
    [InlineData("notes.txt", false, null)]
    public void ServedFile_IsNeverSniffed_AndActiveTypesAreSandboxed(string name, bool download, string? csp)
    {
        var headers = new Microsoft.AspNetCore.Http.HeaderDictionary();
        ServeMode.ApplyFileHeaders(headers, name, download);
        Assert.Equal("nosniff", headers["X-Content-Type-Options"].ToString());
        Assert.Equal(csp, headers.TryGetValue("Content-Security-Policy", out var v) ? v.ToString() : null);
    }

    [Fact]
    public void HandleFile_AppliesTheHeaders_BeforeWritingTheFile()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "MuxSwarm.csproj"))) d = d.Parent;
        var src = File.ReadAllText(Path.Combine(d!.FullName, "Engine", "ServeMode.Files.cs"));
        int a = src.IndexOf("private static async Task HandleFile(", StringComparison.Ordinal);
        int h = src.IndexOf("ApplyFileHeaders(context.Response.Headers", a, StringComparison.Ordinal);
        int f = src.IndexOf("Results.File(", a, StringComparison.Ordinal);
        Assert.True(a > 0 && h > a && h < f, "HandleFile must set the headers before Results.File");
    }
}
