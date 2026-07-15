using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MuxSwarm.Utils;

/// <summary>
/// Ensures all child processes spawned by MuxSwarm are cleaned up on exit.
///
/// Windows: Uses a Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.
///          All child processes are assigned to the job. When MuxSwarm exits
///          (even on crash/SIGINT), the OS kills all processes in the job.
///
/// Unix:    Tracks child PIDs manually. On shutdown, sends SIGTERM then
///          SIGKILL after a grace period. Also sets up SIGTERM handler
///          to forward signals to children.
/// </summary>
public sealed class ProcessCleanup : IDisposable
{
    private static ProcessCleanup? _instance;
    private static readonly Lock InstanceLock = new();

    private readonly List<int> _trackedPids = [];
    private readonly Lock _pidLock = new();
    private bool _disposed;

    // ── Windows Job Object handles ──
    private IntPtr _jobHandle = IntPtr.Zero;

    // ── Shutdown grace period ──
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets the singleton instance, creating it on first access.
    /// The job object (Windows) is created immediately.
    /// </summary>
    public static ProcessCleanup Instance
    {
        get
        {
            if (_instance != null) return _instance;
            lock (InstanceLock)
            {
                _instance ??= new ProcessCleanup();
                return _instance;
            }
        }
    }

    private ProcessCleanup()
    {
        if (PlatformContext.IsWindows)
            InitWindowsJobObject();

        // Register cleanup on process exit
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();

        // Also handle unhandled exceptions
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Shutdown();

        if (!PlatformContext.IsWindows)
        {
            PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx =>
            {
                ctx.Cancel = true;
                Shutdown();
                Environment.Exit(129);
            });
        }
    }

    /// <summary>
    /// Registers a process for cleanup. On Windows, assigns it to the job object.
    /// On Unix, tracks the PID for manual cleanup.
    /// Call this immediately after starting any child process (MCP servers, Docker, etc.)
    /// </summary>
    public void Track(Process process)
    {
        if (_disposed || process.HasExited) return;

        try
        {
            if (PlatformContext.IsWindows && _jobHandle != IntPtr.Zero)
            {
                AssignProcessToJobObject(_jobHandle, process.Handle);
            }

            lock (_pidLock)
            {
                _trackedPids.Add(process.Id);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CLEANUP] Failed to track PID {process.Id}: {ex.Message}");
        }
    }

    /// <summary>
    /// Registers a PID for cleanup when you don't have the Process object.
    /// Useful for processes started by libraries (e.g., MCP SDK).
    /// </summary>
    public void TrackPid(int pid)
    {
        if (_disposed) return;

        lock (_pidLock)
        {
            if (!_trackedPids.Contains(pid))
                _trackedPids.Add(pid);
        }
    }

    /// <summary>
    /// Removes a PID from tracking (e.g., when a process exits normally).
    /// </summary>
    public void Untrack(int pid)
    {
        lock (_pidLock)
        {
            _trackedPids.Remove(pid);
        }
    }

    /// <summary>
    /// Gracefully shuts down all tracked child processes.
    /// Sends SIGTERM (Unix) or calls Kill() (Windows), waits for grace period,
    /// then force-kills any survivors.
    /// </summary>
    public void Shutdown()
    {
        if (_disposed) return;

        List<int> pids;
        lock (_pidLock)
        {
            pids = [.. _trackedPids];
            _trackedPids.Clear();
        }

        // NOTE: do NOT early-return when pids.Count == 0. MCP servers are not Track()ed
        // (only HookWorker processes are), so the tracked-PID list is usually empty even
        // when MCP subprocesses are alive. MCP client disposal must still run; the Windows
        // Job Object is the authoritative backstop on handle close.

        // Phase 1: Graceful termination
        foreach (var pid in pids)
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    if (PlatformContext.IsWindows)
                    {
                        // On Windows the job object handles this, but we also
                        // try graceful close for processes that handle it
                        proc.CloseMainWindow();
                    }
                    else
                    {
                        // Send SIGTERM on Unix
                        proc.Kill(entireProcessTree: false);
                    }
                }
            }
            catch (ArgumentException)
            {
                // Process already exited — expected
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CLEANUP] Graceful shutdown failed for PID {pid}: {ex.Message}");
            }
        }

        //Dispose MCP Clients
        Task.WhenAll(App.McpClients.Values.Select(c => c.DisposeAsync().AsTask()))
            .Wait(TimeSpan.FromSeconds(5));

        // Phase 2: Wait for grace period, then force kill survivors
        var deadline = DateTime.UtcNow + ShutdownGrace;
        foreach (var pid in pids)
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                var remaining = deadline - DateTime.UtcNow;

                if (remaining > TimeSpan.Zero)
                    proc.WaitForExit((int)remaining.TotalMilliseconds);

                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    Debug.WriteLine($"[CLEANUP] Force killed PID {pid}");
                }
            }
            catch (ArgumentException)
            {
                // Already exited
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CLEANUP] Force kill failed for PID {pid}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Returns a snapshot of currently tracked PIDs (for diagnostics).
    /// </summary>
    public List<int> GetTrackedPids()
    {
        lock (_pidLock)
        {
            return [.. _trackedPids];
        }
    }

    private void InitWindowsJobObject()
    {
        if (!PlatformContext.IsWindows) return;

        try
        {
            _jobHandle = CreateJobObject(IntPtr.Zero, $"QweProcessCleanup_{Environment.ProcessId}");
            if (_jobHandle == IntPtr.Zero)
            {
                Debug.WriteLine("[CLEANUP] Failed to create Job Object");
                return;
            }

            // Configure the job to kill all processes when the handle is closed
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    // BREAKAWAY_OK lets a child opt out of the job (CREATE_BREAKAWAY_FROM_JOB) so a
                    // deliberately persistent sidecar (e.g. the CLIProxyAPI subscription proxy) can
                    // survive Mux exit. Other children don't request breakaway, so they're still reaped.
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_BREAKAWAY_OK
                }
            };

            int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr infoPtr = Marshal.AllocHGlobal(length);

            try
            {
                Marshal.StructureToPtr(info, infoPtr, false);
                SetInformationJobObject(_jobHandle, JobObjectInfoType.ExtendedLimitInformation, infoPtr, (uint)length);
            }
            finally
            {
                Marshal.FreeHGlobal(infoPtr);
            }

            // Assign THE CURRENT PROCESS to the job. On Windows, child processes a job member
            // spawns are automatically added to the same job (Win8+ allows nested jobs), so every
            // descendant - MCP stdio servers spawned internally by the MCP library AND their own
            // grandchildren (e.g. chroma-mcp's Python workers) - inherits the job WITHOUT any
            // per-process Track() call. When Mux exits and the handle closes,
            // KILL_ON_JOB_CLOSE terminates the whole tree, so nothing (notably ChromaDB) leaks.
            try
            {
                if (!AssignProcessToJobObject(_jobHandle, GetCurrentProcess()))
                    Debug.WriteLine($"[CLEANUP] AssignProcessToJobObject(self) failed: {Marshal.GetLastWin32Error()}");
            }
            catch (Exception ex) { Debug.WriteLine($"[CLEANUP] self-assign threw: {ex.Message}"); }

            Debug.WriteLine("[CLEANUP] Windows Job Object created + self assigned");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CLEANUP] Job Object init failed: {ex.Message}");
        }
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x00000800;

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, JobObjectInfoType infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);


    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Restore the terminal scroll region if a docked TUI footer was active.
        try { MuxConsole.DisableDockedFooter(); } catch { /* ignore */ }

        Shutdown();

        if (PlatformContext.IsWindows && _jobHandle != IntPtr.Zero)
        {
            // Closing the job handle with KILL_ON_JOB_CLOSE triggers
            // the OS to terminate all processes in the job
            CloseHandle(_jobHandle);
            _jobHandle = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }

    ~ProcessCleanup()
    {
        Dispose();
    }
}