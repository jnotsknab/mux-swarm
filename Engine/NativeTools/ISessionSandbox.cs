namespace MuxSwarm.Engine.NativeTools;

/// <summary>
/// A per-session execution sandbox that shell jobs and the Python worker <c>exec</c> into: a container
/// (<see cref="OciSandbox"/>) or a Docker Sandboxes microVM (<see cref="SbxSandbox"/>).
/// </summary>
internal interface ISessionSandbox : IDisposable
{
    /// <summary>Create the sandbox on first use (or rebuild it if it died). Throws <see cref="SandboxException"/>.</summary>
    void EnsureStarted();

    /// <summary>The (file, args) that run <paramref name="innerCommand"/> through <c>sh -c</c> in /work.</summary>
    (string File, string Args) ExecShell(string innerCommand);

    /// <summary>The (file, args) that run the Python worker with interactive stdio.</summary>
    (string File, string Args) ExecPythonWorker(string guestWorkerPath);
}
