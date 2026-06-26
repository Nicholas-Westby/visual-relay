using System.Text.Json;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for the Windows sandbox seam (Phase 3): the VR-authored MXC policy
/// (confined writes, broad reads, network open), the OS/opt-in mode selection
/// (MXC default, builtin opt-in, blocked when nothing is available), and the
/// wrapper-building for each mode. All pure logic, asserted on any OS.
/// </summary>
public sealed class WindowsSandboxTests
{
    // ── MXC policy generation ────────────────────────────────────────────

    [Fact]
    public void Policy_ConfinesWritesUnderFilesystem_BroadReadDefault_NetworkAllow()
    {
        const string workspace = @"C:\repo";
        var caches = new[] { @"C:\Users\u\AppData\Local", @"C:\Users\u\.nuget\packages" };

        var json = MxcPolicyGenerator.Generate(workspace, caches);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Writes are confined under filesystem.readwritePaths — the real v0.7.0-alpha
        // schema (verified against wxc-exec), NOT a top-level readwritePaths.
        var rw = root.GetProperty("filesystem").GetProperty("readwritePaths")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(workspace, rw[0]); // workspace is the first writable root
        Assert.Contains(@"C:\Users\u\AppData\Local", rw);
        Assert.Contains(@"C:\Users\u\.nuget\packages", rw);
        // The workspace root is the ONLY writable root that is not a cache dir.
        Assert.Equal(new[] { workspace }, rw.Where(p => !caches.Contains(p)).ToArray());

        // Reads are broad by MXC default — VR does not enumerate readonlyPaths.
        Assert.False(root.GetProperty("filesystem").TryGetProperty("readonlyPaths", out _));

        // Network must be opened EXPLICITLY — MXC is deny-by-default since SDK 0.3.0.
        Assert.Equal("allow", root.GetProperty("network").GetProperty("defaultPolicy").GetString());
    }

    [Fact]
    public void Policy_PinsTheMxcSchemaVersion()
    {
        var json = MxcPolicyGenerator.Generate(@"C:\repo", []);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(MxcPolicyGenerator.PinnedMxcVersion, doc.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public void DefaultWindowsCacheDirs_ReturnsOnlyExistingDirs()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows cache dirs read the real environment");
        // MXC's AppContainer+DACL backend fails to stamp an ACE on a missing dir, so a
        // non-existent cache root (e.g. ~/.cargo on a non-Rust host) must never reach
        // the policy — every returned dir must actually exist.
        foreach (var dir in MxcPolicyGenerator.DefaultWindowsCacheDirs())
            Assert.True(Directory.Exists(dir), $"cache dir should exist: {dir}");
    }

    // ── Mode selection ───────────────────────────────────────────────────

    [Fact]
    public void Select_MxcAvailable_DefaultsToMxc()
    {
        Assert.Equal(WindowsSandboxMode.Mxc, WindowsSandbox.Select(optIn: null, mxcAvailable: true));
    }

    [Fact]
    public void Select_MxcAbsent_NoOptIn_IsBlocked()
    {
        Assert.Equal(WindowsSandboxMode.Blocked, WindowsSandbox.Select(optIn: null, mxcAvailable: false));
    }

    [Fact]
    public void Select_BuiltinOptIn_OverridesEvenWhenMxcAbsent()
    {
        Assert.Equal(WindowsSandboxMode.Builtin, WindowsSandbox.Select(optIn: "builtin", mxcAvailable: false));
    }

    // ── Wrapper building ─────────────────────────────────────────────────

    [Fact]
    public void BuildMxcLaunch_SeparatesCommandWithDoubleDash()
    {
        var (fileName, args) = WindowsSandbox.BuildMxcLaunch(
            @"C:\mxc\wxc-exec.exe", @"C:\cfg\policy.json", "swival", ["-q", "--report", "r.json"]);

        Assert.Equal(@"C:\mxc\wxc-exec.exe", fileName);
        // wxc-exec needs `<config> -- <command>`: the `--` separator is REQUIRED, else
        // it parses the program as its own flags (verified against wxc-exec v0.7.0-rc1).
        Assert.Equal(new[] { @"C:\cfg\policy.json", "--", "swival", "-q", "--report", "r.json" }, args);
    }

    [Fact]
    public void BuildBuiltinSwivalLaunch_AppendsSandboxFlag_NoWrapper()
    {
        var (fileName, args) = WindowsSandbox.BuildBuiltinSwivalLaunch("swival", ["-q", "--report", "r.json"]);

        Assert.Equal("swival", fileName); // swival self-sandboxes; no external wrapper
        Assert.Equal(new[] { "-q", "--report", "r.json", "--sandbox", "builtin" }, args);
    }

    // ── Surfacing the active mode + the blocked guidance ─────────────────

    [Fact]
    public void BlockedMessage_GivesActionableInstall_AndOptIn()
    {
        Assert.Contains("wxc-exec", WindowsSandbox.BlockedMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("builtin", WindowsSandbox.BlockedMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribeMode_FlagsBuiltinAsDegraded()
    {
        Assert.Contains("degraded", WindowsSandbox.DescribeMode(WindowsSandboxMode.Builtin), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MXC", WindowsSandbox.DescribeMode(WindowsSandboxMode.Mxc), StringComparison.OrdinalIgnoreCase);
    }
}
