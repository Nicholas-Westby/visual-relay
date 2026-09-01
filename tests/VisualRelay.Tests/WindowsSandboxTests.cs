using System.Text.Json;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for the Windows sandbox seam (Phase 3): the VR-authored MXC policy
/// (confined writes, broad reads, network open), the mode selection (MXC when
/// available, blocked when it is not — there is no unsandboxed opt-out), and the
/// MXC wrapper-building. All pure logic, asserted on any OS.
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
    public void Select_MxcAvailable_IsMxc()
    {
        Assert.Equal(WindowsSandboxMode.Mxc, WindowsSandbox.Select(mxcAvailable: true));
    }

    [Fact]
    public void Select_MxcAbsent_IsBlocked()
    {
        // There is no opt-in that trades the container away for an unconfined run:
        // MXC-or-blocked, the same all-or-nothing rule the nono arm follows.
        Assert.Equal(WindowsSandboxMode.Blocked, WindowsSandbox.Select(mxcAvailable: false));
    }

    // ── Wrapper building ─────────────────────────────────────────────────

    [Fact]
    public void BuildMxcLaunch_SeparatesCommandWithDoubleDash()
    {
        var (fileName, args) = WindowsSandbox.BuildMxcLaunch(
            @"C:\mxc\wxc-exec.exe", @"C:\cfg\policy.json", "dotnet", ["test", "--nologo"]);

        Assert.Equal(@"C:\mxc\wxc-exec.exe", fileName);
        // wxc-exec needs `<config> -- <command>`: the `--` separator is REQUIRED, else
        // it parses the program as its own flags (verified against wxc-exec v0.7.0-rc1).
        Assert.Equal(new[] { @"C:\cfg\policy.json", "--", "dotnet", "test", "--nologo" }, args);
    }

    // ── Surfacing the active mode + the blocked guidance ─────────────────

    [Fact]
    public void BlockedMessage_GivesActionableInstall_AndNoUnsandboxedEscape()
    {
        Assert.Contains("wxc-exec", WindowsSandbox.BlockedMessage, StringComparison.OrdinalIgnoreCase);
        // The message must never advertise a way to run without confinement.
        Assert.DoesNotContain("opt", WindowsSandbox.BlockedMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribeMode_NamesTheContainer_AndTheBlockedState()
    {
        Assert.Contains("MXC", WindowsSandbox.DescribeMode(WindowsSandboxMode.Mxc), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("blocked", WindowsSandbox.DescribeMode(WindowsSandboxMode.Blocked), StringComparison.OrdinalIgnoreCase);
    }
}
