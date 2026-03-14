using MuxSwarm.Utils;

namespace MuxSwarm.Tests.Tests;
using Setup;

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
    
}
