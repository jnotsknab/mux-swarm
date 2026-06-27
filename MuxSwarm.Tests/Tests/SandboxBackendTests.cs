using System;
using System.Collections.Generic;
using MuxSwarm.Utils;
using MuxSwarm.Utils.NativeTools;
using Xunit;

namespace MuxSwarm.Tests.Tests;

/// <summary>
/// Unit coverage for the pluggable sandbox backend resolver + validator. Pure config -> spec logic;
/// does NOT spawn containers (those need a live docker daemon and are validated interactively).
/// Backends whose binary is absent on the test host resolve to a validation ERROR, which is itself the
/// contract under test for the "missing binary => hard error, never silent host fallback" rule.
/// </summary>
[Collection("ConsoleState")]
public class SandboxBackendTests
{
    private static SandboxConfig Cfg(string backend, string image = "python:3.12-slim",
        bool network = false, List<string>? allow = null, string command = "") =>
        new() { Backend = backend, Image = image, Network = network, AllowedDomains = allow ?? new(), Command = command };

    [Fact]
    public void Host_ResolvesToNull_NoSandbox()
    {
        Assert.Null(SandboxBackend.Resolve(Cfg("host")));
        Assert.Null(SandboxBackend.Resolve(Cfg("")));
        Assert.Null(SandboxBackend.Resolve(Cfg("none")));
        Assert.Null(SandboxBackend.Validate(Cfg("host")));   // host is always valid
    }

    [Fact]
    public void UnknownBackend_IsRejected()
    {
        var err = SandboxBackend.Validate(Cfg("garbage-backend"));
        Assert.NotNull(err);
        Assert.Contains("Unknown sandbox.backend", err);
    }

    [Fact]
    public void Custom_RequiresTemplate()
    {
        var err = SandboxBackend.Validate(Cfg("custom", command: ""));
        Assert.NotNull(err);
        Assert.Contains("template", err);
        // With a template it resolves (custom never probes a binary).
        Assert.Null(SandboxBackend.Validate(Cfg("custom", command: "docker run {image} sh -c {cmd}")));
    }

    [Fact]
    public void Custom_RejectsAllowlist()
    {
        var err = SandboxBackend.Validate(Cfg("custom", command: "x {cmd}", allow: new() { "pypi.org" }));
        Assert.NotNull(err);
        Assert.Contains("OCI", err);
    }

    [Fact]
    public void Allowlist_OnWrapperBackend_IsRejected()
    {
        // bwrap/firejail are Linux-only; on a non-Linux host the OS check may fire first, but EITHER way
        // an allowlist on a wrapper backend must NOT validate.
        Assert.NotNull(SandboxBackend.Validate(Cfg("bwrap", allow: new() { "pypi.org" })));
        Assert.NotNull(SandboxBackend.Validate(Cfg("firejail", allow: new() { "pypi.org" })));
    }

    [Fact]
    public void WrapperBackends_AreOsGated()
    {
        if (OperatingSystem.IsWindows())
        {
            // None of the wrapper backends are valid on Windows.
            Assert.NotNull(SandboxBackend.Validate(Cfg("bwrap")));
            Assert.NotNull(SandboxBackend.Validate(Cfg("firejail")));
            Assert.NotNull(SandboxBackend.Validate(Cfg("sandbox-exec")));
        }
    }

    [Fact]
    public void CustomSpec_RendersTemplate()
    {
        var spec = SandboxBackend.Resolve(Cfg("custom", image: "myimg", command: "run {image} :: {workdir} :: {cmd}"));
        Assert.NotNull(spec);
        Assert.Equal(SandboxKind.Custom, spec!.Kind);
        var (file, args) = SandboxBackend.WrapShellCommand(spec, "echo hi", "/work/dir");
        // The rendered template (after placeholder substitution) appears in the shell-wrapped args.
        Assert.Contains("myimg", args);
        Assert.Contains("/work/dir", args);
        Assert.Contains("echo hi", args);
    }

    [Fact]
    public void OciBackend_MissingBinary_IsHardError_NeverSilentHost()
    {
        // If docker is not installed/ready on the test host, validation MUST return an error (the
        // anti-silent-fallback contract). If docker IS present+ready, it validates - both are correct;
        // the invariant is "never null-with-an-unusable-backend".
        var err = SandboxBackend.Validate(Cfg("docker"));
        if (err is not null)
            Assert.True(err.Contains("docker", StringComparison.OrdinalIgnoreCase));
        else
            Assert.NotNull(SandboxBackend.Resolve(Cfg("docker"))); // present => resolves to a real spec
    }
}
