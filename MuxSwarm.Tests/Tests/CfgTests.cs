using MuxSwarm.Engine;

namespace MuxSwarm.Tests.Tests;
using Setup;

[Collection("ConsoleState")]
public class CfgTests
{   
    
    private readonly TextWriter _originalOut = Console.Out;

    public CfgTests()
    {
        Console.SetOut(TextWriter.Null);
        MuxConsole.StdioMode = true;
    }

    internal void Dispose()
    {
        Console.SetOut(_originalOut);
        MuxConsole.InputOverride = Console.In;
        MuxConsole.StdioMode = false;
    }
    
    [Fact]
    public void LoadConfig_FileNotFound_CreatesDefaultCfg()
    {
        var tmpPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "Config.json");

        try
        {
            var config = Setup.LoadConfig(tmpPath);
            Assert.NotNull(config);
            Assert.True(File.Exists(tmpPath));
            
        }
        finally
        {
            var dir = Path.GetDirectoryName(tmpPath);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
    
    [Fact]
    public void LoadConfig_DefaultCfgCreated_SetupNotCompleted()
    {
        var tmpPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "Config.json");

        try
        {
            AppConfig config = Setup.LoadConfig(tmpPath);
            Assert.False(config.SetupCompleted);
            
        }
        finally
        {
            var dir = Path.GetDirectoryName(tmpPath);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
    
    [Fact]
    public void RunSetup_SetupCancel_SetupFailed()
    {   
        try
        {   
            MuxConsole.StdioMode = true;
            MuxConsole.InputOverride = new StringReader("");
            bool complete = Setup.RunSetup();
            Assert.False(complete);

        }
        catch (Exception e)
        {
            Assert.Fail(e.Message);
        }
    }
    
    [Fact]
    public void RunSetup_EmptyInput_ReturnsFalse()
    {
        MuxConsole.InputOverride = new StringReader("");
        Assert.False(Setup.RunSetup());
    }
    
    [Fact]
    public void RunSetup_InvalidFilePaths_SetupFailed()
    {   
        try
        {   
            MuxConsole.StdioMode = true;
            MuxConsole.InputOverride = new StringReader(@"C:\Users\idk\path\not\exist");
            bool complete = Setup.RunSetup();
            Assert.False(complete);

        }
        catch (Exception e)
        {
            Assert.Fail(e.Message);
        }
    }

    [Fact]
    public void DescribeTuiDefaults_DefaultConfig_ReportsFrameAndButtons()
    {
        var line = Setup.DescribeTuiDefaults(new AppConfig());

        Assert.Contains("frame renderer", line);
        Assert.Contains("mouse buttons", line);
        Assert.Contains("/set renderEngine", line);
        Assert.Contains("/mouse", line);
    }

    [Fact]
    public void DescribeTuiDefaults_ReadsLiveConfigValues_NotHardcodedDefaults()
    {
        var config = new AppConfig
        {
            Console = new ConsoleConfig { RenderEngine = "inline", MouseTracking = "wheel" }
        };

        var line = Setup.DescribeTuiDefaults(config);

        Assert.Contains("inline renderer", line);
        Assert.Contains("mouse wheel", line);
        Assert.DoesNotContain("frame renderer", line);
    }

    [Fact]
    public void DescribeTuiDefaults_NullConsoleAndBlankValues_FallBackToDefaults()
    {
        var nullConsole = Setup.DescribeTuiDefaults(new AppConfig { Console = null! });
        Assert.Contains("frame renderer", nullConsole);
        Assert.Contains("mouse buttons", nullConsole);

        var blank = Setup.DescribeTuiDefaults(new AppConfig
        {
            Console = new ConsoleConfig { RenderEngine = " ", MouseTracking = "" }
        });
        Assert.Contains("frame renderer", blank);
        Assert.Contains("mouse buttons", blank);
    }

    [Fact]
    public void PatchCommandsFromDepResults_FetchIsNpx_PatchedToAbsoluteNpxPath()
    {
        var config = new AppConfig();
        McpServerDefaults.EnsureDefaultsPresent(config);
        Assert.Equal("npx", config.McpServers["Fetch"].Command);

        var npxPath = PlatformContext.IsWindows ? @"C:\tools\node\npx.cmd" : "/usr/local/bin/npx";
        var uvxPath = PlatformContext.IsWindows ? @"C:\tools\uv\uvx.exe" : "/usr/local/bin/uvx";
        var results = new List<DepResolver.DepResult>
        {
            new(new DepResolver.Dep("npx", "test"), true, true, true, npxPath, false, false, false),
            new(new DepResolver.Dep("uvx", "test"), true, true, true, uvxPath, false, false, false),
        };

        McpServerDefaults.PatchCommandsFromDepResults(config, results);

        Assert.Equal(npxPath, config.McpServers["Fetch"].Command);
        Assert.Equal(npxPath, config.McpServers["Memory"].Command);
        Assert.Equal(npxPath, config.McpServers["BraveSearchMCP"].Command);
        Assert.Equal(uvxPath, config.McpServers["ChromaDB"].Command);
    }

    [Fact]
    public void PatchCommandsFromDepResults_NativeAndCustomEntriesUntouched()
    {
        var config = new AppConfig();
        McpServerDefaults.EnsureDefaultsPresent(config);
        config.McpServers["Fetch"].Command = "/opt/custom/my-npx";

        var results = new List<DepResolver.DepResult>
        {
            new(new DepResolver.Dep("npx", "test"), true, true, true, "/resolved/npx", false, false, false),
            new(new DepResolver.Dep("uvx", "test"), true, true, true, "/resolved/uvx", false, false, false),
        };

        McpServerDefaults.PatchCommandsFromDepResults(config, results);

        // Native in-process entries keep their marker; user-customized commands are never overwritten.
        Assert.Equal("native-runtime-tools", config.McpServers["Filesystem"].Command);
        Assert.Equal("native-runtime-tools", config.McpServers["Shell"].Command);
        Assert.Equal("/opt/custom/my-npx", config.McpServers["Fetch"].Command);
    }
}
