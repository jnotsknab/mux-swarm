using System.Diagnostics;
using System.Reflection;
using MuxSwarm.Utils;
using Xunit;

namespace MuxSwarm.Tests.Tests;

[Collection("ConsoleState")]
public class ProcessCleanupTests
{
    [Fact]
    public void ProcessCleanup_HasNoPidDerivedProcessGroupSweep()
    {
        const BindingFlags AllMethods =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Static | BindingFlags.Instance;

        var methods = typeof(ProcessCleanup).GetMethods(AllMethods);

        Assert.DoesNotContain(methods, method => method.Name == "KillOwnProcessTree");
    }

    [Fact]
    public void Shutdown_TerminatesTrackedChild_WithoutTerminatingCaller()
    {
        using var child = StartLongRunningChild();
        try
        {
            ProcessCleanup.Instance.Track(child);
            Assert.Contains(child.Id, ProcessCleanup.Instance.GetTrackedPids());

            ProcessCleanup.Instance.Shutdown();

            Assert.True(child.WaitForExit(10_000), $"Tracked child {child.Id} did not exit.");
            Assert.True(child.HasExited);
        }
        finally
        {
            if (!child.HasExited)
                child.Kill(entireProcessTree: true);
            ProcessCleanup.Instance.Untrack(child.Id);
        }
    }

    [Fact]
    public void WindowsCleanup_UsesSelfAssignedKillOnCloseJobObject()
    {
        var sourcePath = Path.Combine(
            FindRepositoryRoot(), "Utils", "ProcessCleanup.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.Contains(
            "JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_BREAKAWAY_OK",
            source);
        Assert.Contains(
            "AssignProcessToJobObject(_jobHandle, GetCurrentProcess())",
            source);
        Assert.Contains("CloseHandle(_jobHandle)", source);
    }

    [Fact]
    public void Shutdown_DisposesAllRegisteredMcpClients()
    {
        var sourcePath = Path.Combine(
            FindRepositoryRoot(), "Utils", "ProcessCleanup.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.Contains(
            "App.McpClients.Values.Select(c => c.DisposeAsync().AsTask())",
            source);
        Assert.Contains(".Wait(TimeSpan.FromSeconds(5))", source);
    }

    private static Process StartLongRunningChild()
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/d /c ping -t 127.0.0.1 > nul")
            : new ProcessStartInfo("/bin/sh", "-c \"sleep 300\"");
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start cleanup test child.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MuxSwarm.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the MuxSwarm repository root.");
    }
}
