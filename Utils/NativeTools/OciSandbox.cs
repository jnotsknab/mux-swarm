using System.Diagnostics;
using System.Text;

namespace MuxSwarm.Utils.NativeTools;

/// <summary>
/// One per-session OCI sandbox: a persistent container (mounting the session work dir at /work) that
/// the session's shell jobs AND its Python REPL worker execute inside via `&lt;binary&gt; exec`. This is the
/// all-or-nothing model - both exec surfaces live in the same container so model code cannot escape to
/// the host by choosing the un-sandboxed tool. Lifecycle mirrors the lazy python-worker model: nothing
/// is created until first use; <see cref="Dispose"/> force-removes the container (and the allowlist
/// proxy + internal network, if any). Created/owned by a <see cref="ReplSession"/>.
///
/// Network allowlist: when <see cref="SandboxSpec.UsesAllowlist"/>, the container is attached ONLY to a
/// Docker `--internal` network (no host egress); a tiny filtering-proxy sidecar (python from the same
/// base image) sits on both that internal net and a normal net and CONNECT-filters by host against the
/// allowlist. The sandbox's HTTP(S)_PROXY point at the sidecar, so it can reach listed domains only.
/// </summary>
internal sealed class OciSandbox : IDisposable
{
    private readonly SandboxSpec _spec;
    private readonly string _hostWorkDir;
    private readonly string _key;
    private readonly object _gate = new();
    private bool _started;
    private bool _disposed;
    // Self-heal guard: bound consecutive (re)build attempts so a container that dies IMMEDIATELY every
    // time (bad image, cap conflict, mount denied) surfaces the real error once instead of looping.
    private int _buildAttempts;
    private string _lastBuildError = "";
    private const int MaxBuildAttempts = 3;

    private string _containerName = "";
    private string _proxyName = "";
    private string _netName = "";

    public const string GuestWorkDir = "/work";

    public OciSandbox(SandboxSpec spec, string hostWorkDir, string key)
    {
        _spec = spec;
        _hostWorkDir = hostWorkDir;
        _key = key;
    }

    /// <summary>
    /// Ensure the per-session container (and proxy/network if an allowlist is active) exist AND are
    /// running. Self-healing: if we created one before but it has since died/been removed (OOM, crash,
    /// daemon restart, manual docker rm), it is rebuilt rather than left dead. Idempotent when already
    /// healthy. Throws SandboxException only when the backend itself is unusable (e.g. daemon down).
    /// </summary>
    public void EnsureStarted()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_started)
            {
                if (ContainerRunning()) return;       // healthy - nothing to do
                // Container is gone/dead: tear down stale proxy+net and rebuild from scratch.
                RebuildTeardown_NoLock();
            }

            // Bound rebuild attempts: if the container will not stay up, stop hammering the daemon and
            // surface the real docker error (captured below) so the user/model sees WHY, not a silent loop.
            if (_buildAttempts >= MaxBuildAttempts)
                throw new SandboxException(
                    $"sandbox container failed to start {MaxBuildAttempts}x and will not be retried this session. " +
                    $"Last error: {(_lastBuildError.Length > 0 ? _lastBuildError : "unknown")}. " +
                    "Check the image, docker daemon, and `/sandbox` config (or set sandbox.backend host).");
            _buildAttempts++;

            string sfx = Sanitize(_key) + "_" + Guid.NewGuid().ToString("N")[..6];
            _containerName = "mux_sbx_" + sfx;
            Directory.CreateDirectory(_hostWorkDir);

            string netArg;
            if (_spec.UsesAllowlist)
            {
                // 1) internal (egress-less) network the sandbox lives on.
                _netName = "mux_sbxnet_" + sfx;
                Run(_spec.Binary, $"network create --internal {_netName}", allowFail: false);
                // 2) the filtering proxy sidecar: on the internal net AND a normal (egress) net.
                _proxyName = "mux_sbxproxy_" + sfx;
                StartProxy(sfx);
                netArg = $"--network {_netName}";
            }
            else
            {
                netArg = _spec.NetworkOpen ? "" : "--network none";
            }

            string runtimeArg = _spec.Runtime is { } rt ? $"--runtime={rt}" : "";
            // Hardening for untrusted execution (matches the posture audit's follow-ups): drop ALL
            // Linux capabilities and forbid privilege escalation via setuid binaries. We keep the
            // container's default user (many base images need root for pip/apt); userns-remap is a
            // daemon-level choice the operator can enable separately so container-root != host-root.
            const string hardenArg = "--cap-drop=ALL --security-opt=no-new-privileges";
            // Proxy env so the sandbox routes HTTP(S) through the sidecar when an allowlist is active.
            string proxyEnv = _spec.UsesAllowlist
                ? $"-e HTTP_PROXY=http://{_proxyName}:8080 -e HTTPS_PROXY=http://{_proxyName}:8080 -e http_proxy=http://{_proxyName}:8080 -e https_proxy=http://{_proxyName}:8080"
                : "";

            // Persistent container: sleep forever, we exec into it. /work is the Mux-internal scratch
            // (always RW); host AllowedPaths are bound at /host/<leaf> with RO/RW per the fs security
            // posture (see SandboxBackend.ResolveMounts) so sandboxed code works on the real project.
            var binds = new System.Text.StringBuilder();
            binds.Append("-v ").Append(MountSpec(_hostWorkDir, GuestWorkDir, readOnly: false));
            foreach (var m in _spec.Mounts)
                binds.Append(" -v ").Append(MountSpec(m.HostPath, m.GuestPath, m.ReadOnly));
            string args = $"run -d --name {_containerName} {runtimeArg} {hardenArg} {netArg} {proxyEnv} " +
                          $"{binds} -w {GuestWorkDir} " +
                          $"--entrypoint sh {_spec.Image} -c \"sleep infinity\"";
            var (ok, _, err) = Run(_spec.Binary, args, allowFail: true);
            if (!ok)
            {
                _lastBuildError = err.Trim();
                // Tear down partial state but DO NOT dispose the whole object - we want to allow a bounded
                // retry on the next tool call (transient daemon hiccups self-heal); the attempt counter
                // stops a hard loop.
                RebuildTeardown_NoLock();
                throw new SandboxException($"failed to start sandbox container ({_spec.Backend}): {_lastBuildError}");
            }
            // Confirm it actually STAYED up (an image whose entrypoint exits immediately would pass `run`
            // but not be running) before we trust it. If it died on boot, capture logs as the error.
            _started = true;
            if (!ContainerRunning())
            {
                var (_, logs, lerr) = Run(_spec.Binary, $"logs {_containerName}", allowFail: true);
                _lastBuildError = (logs + lerr).Trim();
                RebuildTeardown_NoLock();
                throw new SandboxException($"sandbox container exited immediately ({_spec.Backend}). " +
                    $"Image entrypoint may be wrong or a dropped capability is required. Detail: {_lastBuildError}");
            }
            _buildAttempts = 0;          // healthy - reset the self-heal budget
            _lastBuildError = "";
        }
    }

    private void StartProxy(string sfx)
    {
        // Write a tiny CONNECT-filtering proxy script and run it from the SAME base image (no extra pull).
        // It allows CONNECT only to allowlisted hosts (suffix match), denies everything else. Plain HTTP
        // is also filtered by Host header. ~deny-by-default.
        string allowPy = "[" + string.Join(",", _spec.AllowedDomains.Select(d => "\\\"" + d.Replace("\"", "") + "\\\"")) + "]";
        // Normalize to LF: the verbatim ProxyScript carries the file's CRLF endings; base64 it as LF
        // so the python written to /tmp/p.py inside the container is clean.
        string script = ProxyScript.Replace("\r\n", "\n").Replace("__ALLOW__", allowPy);
        // base64 the script in so we don't fight shell quoting across platforms.
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        _netName = string.IsNullOrEmpty(_netName) ? ("mux_sbxnet_" + sfx) : _netName;
        // proxy joins the internal net (alias used by the sandbox) AND gets normal egress via a second net.
        string args = $"run -d --name {_proxyName} --network {_netName} " +
                      $"-e MUX_PROXY_B64={b64} --entrypoint sh {_spec.Image} -c " +
                      "\"echo $MUX_PROXY_B64 | base64 -d > /tmp/p.py && python /tmp/p.py\"";
        var (ok, _, err) = Run(_spec.Binary, args, allowFail: true);
        if (!ok) throw new SandboxException($"failed to start sandbox network proxy: {err.Trim()}");
        // give the proxy a normal egress path too (second network with default bridge).
        Run(_spec.Binary, $"network connect bridge {_proxyName}", allowFail: true);
    }

    /// <summary>
    /// Build the (file,args) to run a one-off shell command inside the container via `exec`.
    /// </summary>
    public (string File, string Args) ExecShell(string innerCommand)
    {
        EnsureStarted();
        string args = $"exec -w {GuestWorkDir} {_containerName} sh -c {ShQuoteForArgv(innerCommand)}";
        return (_spec.Binary, args);
    }

    /// <summary>
    /// Build the (file,args) to run the Python worker inside the container with interactive stdio
    /// (`exec -i`), so the JSON line protocol flows through the container boundary. The worker file is
    /// at <paramref name="guestWorkerPath"/> (already written into the mounted work dir, visible at /work).
    /// </summary>
    public (string File, string Args) ExecPythonWorker(string guestWorkerPath)
    {
        EnsureStarted();
        string args = $"exec -i -w {GuestWorkDir} {_containerName} python {guestWorkerPath}";
        return (_spec.Binary, args);
    }

    /// <summary>True when the session container exists AND is in the running state. Cheap docker inspect.</summary>
    private bool ContainerRunning()
    {
        if (string.IsNullOrEmpty(_containerName)) return false;
        // `inspect -f {{.State.Running}}` prints "true"/"false"; non-zero exit => container absent.
        var (ok, outp, _) = Run(_spec.Binary,
            $"inspect -f {{{{.State.Running}}}} {_containerName}", allowFail: true);
        return ok && outp.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Tear down a dead container's leftovers before a rebuild (force-rm container/proxy, rm net).</summary>
    private void RebuildTeardown_NoLock()
    {
        if (!string.IsNullOrEmpty(_containerName)) Run(_spec.Binary, $"rm -f {_containerName}", allowFail: true);
        if (!string.IsNullOrEmpty(_proxyName)) Run(_spec.Binary, $"rm -f {_proxyName}", allowFail: true);
        if (!string.IsNullOrEmpty(_netName)) Run(_spec.Binary, $"network rm {_netName}", allowFail: true);
        _containerName = ""; _proxyName = ""; _netName = "";
        _started = false;
    }

    public void Dispose()
    {
        lock (_gate) DisposeNoLock();
    }

    private void DisposeNoLock()
    {
        if (_disposed) return;
        _disposed = true;
        if (!string.IsNullOrEmpty(_containerName)) Run(_spec.Binary, $"rm -f {_containerName}", allowFail: true);
        if (!string.IsNullOrEmpty(_proxyName)) Run(_spec.Binary, $"rm -f {_proxyName}", allowFail: true);
        if (!string.IsNullOrEmpty(_netName)) Run(_spec.Binary, $"network rm {_netName}", allowFail: true);
    }

    // ---- helpers ----

    private static (bool ok, string outp, string err) Run(string file, string args, bool allowFail)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file, Arguments = args,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return (false, "", "could not start " + file);
            string o = p.StandardOutput.ReadToEnd();
            string e = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(60000)) { try { p.Kill(true); } catch { } return (false, o, "timed out"); }
            bool ok = p.ExitCode == 0;
            if (!ok && !allowFail) throw new SandboxException($"{file} {args} failed: {e.Trim()}");
            return (ok, o, e);
        }
        catch (SandboxException) { throw; }
        catch (Exception ex) { if (!allowFail) throw new SandboxException(ex.Message); return (false, "", ex.Message); }
    }

    // Mount spec: on Windows host the path is C:\... which docker maps fine via -v. Append :ro for a
    // read-only bind (host data the sandbox may read but not modify under the active fs security mode).
    private static string MountSpec(string hostDir, string guest, bool readOnly) =>
        "\"" + hostDir + "\":" + guest + (readOnly ? ":ro" : "");

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.Length == 0 ? "s" : sb.ToString().ToLowerInvariant();
    }

    // Quote a command for use as a single argv token after `sh -c` in the exec arg string.
    private static string ShQuoteForArgv(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$") + "\"";

    // The injected filtering proxy. Deny-by-default CONNECT + HTTP Host filtering, suffix match on allowlist.
    private const string ProxyScript = @"
import socket, threading, select, sys
ALLOW = __ALLOW__
def host_ok(h):
    h = (h or '').split(':')[0].lower().strip('.')
    for a in ALLOW:
        a = a.lower().strip('.')
        if h == a or h.endswith('.' + a):
            return True
    return False
def pipe(a, b):
    try:
        while True:
            r, _, _ = select.select([a, b], [], [], 60)
            if not r: break
            for s in r:
                d = s.recv(65536)
                if not d:
                    return
                (b if s is a else a).sendall(d)
    except Exception:
        pass
    finally:
        try: a.close()
        except Exception: pass
        try: b.close()
        except Exception: pass
def handle(c):
    try:
        c.settimeout(30)
        data = b''
        while b'\r\n\r\n' not in data:
            chunk = c.recv(4096)
            if not chunk: c.close(); return
            data += chunk
        line = data.split(b'\r\n')[0].decode('latin1')
        parts = line.split(' ')
        method = parts[0]
        if method.upper() == 'CONNECT':
            hostport = parts[1]
            host, _, port = hostport.partition(':')
            port = int(port or '443')
            if not host_ok(host):
                c.sendall(b'HTTP/1.1 403 Forbidden\r\n\r\n'); c.close(); return
            up = socket.create_connection((host, port), timeout=15)
            c.sendall(b'HTTP/1.1 200 Connection Established\r\n\r\n')
            pipe(c, up)
        else:
            host = ''
            for h in data.split(b'\r\n'):
                if h.lower().startswith(b'host:'):
                    host = h.split(b':',1)[1].decode('latin1').strip()
            if not host_ok(host):
                c.sendall(b'HTTP/1.1 403 Forbidden\r\n\r\n'); c.close(); return
            hh = host.split(':')[0]; pp = int(host.split(':')[1]) if ':' in host else 80
            up = socket.create_connection((hh, pp), timeout=15)
            up.sendall(data)
            pipe(c, up)
    except Exception:
        try: c.close()
        except Exception: pass
def main():
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind(('0.0.0.0', 8080)); s.listen(64)
    while True:
        c, _ = s.accept()
        threading.Thread(target=handle, args=(c,), daemon=True).start()
main()
";
}
