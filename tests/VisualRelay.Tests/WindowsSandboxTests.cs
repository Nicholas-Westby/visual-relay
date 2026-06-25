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
    public void Policy_ConfinesWrites_BroadReads_NetworkOpen()
    {
        const string workspace = @"C:\repo";
        var caches = new[] { @"C:\Users\u\AppData\Local", @"C:\Users\u\.nuget\packages" };
        var readonlyRoots = new[] { @"C:\" };

        var json = MxcPolicyGenerator.Generate(workspace, caches, readonlyRoots);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var rw = root.GetProperty("readwritePaths").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(workspace, rw[0]); // workspace is the first writable root
        Assert.Contains(@"C:\Users\u\AppData\Local", rw);
        Assert.Contains(@"C:\Users\u\.nuget\packages", rw);

        var ro = root.GetProperty("readonlyPaths").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(@"C:\", ro);

        Assert.True(root.GetProperty("network").GetProperty("allowOutbound").GetBoolean());

        // The workspace root is the ONLY writable root that is not a cache dir.
        Assert.Equal(new[] { workspace }, rw.Where(p => !caches.Contains(p)).ToArray());
    }

    [Fact]
    public void Policy_PinsTheMxcVersion()
    {
        var json = MxcPolicyGenerator.Generate(@"C:\repo", [], [@"C:\"]);
        Assert.Contains(MxcPolicyGenerator.PinnedMxcVersion, json, StringComparison.Ordinal);
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
    public void BuildMxcLaunch_WrapsProgramAfterPolicy()
    {
        var (fileName, args) = WindowsSandbox.BuildMxcLaunch(
            @"C:\mxc\wxc-exec.exe", @"C:\cfg\policy.json", "swival", ["-q", "--report", "r.json"]);

        Assert.Equal(@"C:\mxc\wxc-exec.exe", fileName);
        Assert.Equal(new[] { @"C:\cfg\policy.json", "swival", "-q", "--report", "r.json" }, args);
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
