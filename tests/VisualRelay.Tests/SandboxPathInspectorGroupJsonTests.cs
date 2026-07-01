using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Group-JSON and integration tests for <see cref="SandboxPathInspector"/>,
/// declared as a partial of <see cref="SandboxPathInspectorTests"/> so helpers
/// defined there are shared.
/// </summary>
public sealed partial class SandboxPathInspectorTests
{
    // ── Group JSON parsing (nono profile groups --json output) ───────────
    [Fact]
    public void ParseGroupJson_ClassifiesAllowReadAllowReadwriteDenyAccess()
    {
        var groupJson = SampleAllowGroupJson();
        const string groupName = "test_runtime";

        var entries = SandboxPathInspector.ParseGroupJson(groupJson, groupName);

        // allow.read → ReadOnly
        var readable = entries.Where(e => e.Access == SandboxAccess.ReadOnly).ToList();
        Assert.NotEmpty(readable);
        Assert.Contains(readable, e => e.Raw == "/usr/local/bin" && e.Source == groupName);

        // allow.readwrite → ReadWrite
        var writable = entries.Where(e => e.Access == SandboxAccess.ReadWrite).ToList();
        Assert.NotEmpty(writable);
        Assert.Contains(writable, e => e.Raw == "/tmp/runtime-cache" && e.Source == groupName);

        // deny.access → Blocked
        var blocked = entries.Where(e => e.Access == SandboxAccess.Blocked).ToList();
        Assert.NotEmpty(blocked);
        Assert.Contains(blocked, e => e.Raw == "/etc/secret" && e.Source == groupName);
    }

    [Fact]
    public void ParseGroupJson_IgnoresDenyCommandsAndDenyUnlink()
    {
        var groupJson = SampleDenyGroupJson();
        const string groupName = "deny_test";

        var entries = SandboxPathInspector.ParseGroupJson(groupJson, groupName);

        // deny.commands entries (e.g. "curl", "wget") must NOT appear in any bucket.
        Assert.DoesNotContain(entries, e => e.Raw == "curl");
        Assert.DoesNotContain(entries, e => e.Raw == "wget");

        // deny.unlink entries must NOT appear in any bucket.
        Assert.DoesNotContain(entries, e => e.Raw == "/var/run/important.pid");

        // deny.access entries (filesystem paths) MUST still appear as Blocked.
        Assert.Contains(entries, e => e.Raw == "/etc/secret" && e.Access == SandboxAccess.Blocked);
        Assert.Contains(entries, e => e.Raw == "/root/.ssh" && e.Access == SandboxAccess.Blocked);
    }

    [Fact]
    public void ParseGroupJson_FiltersByPlatform()
    {
        var json = SampleCrossPlatformGroupJson();
        const string groupName = "cross_plat";

        var entries = SandboxPathInspector.ParseGroupJson(json, groupName);

        // "cross-platform" entries are always included.
        Assert.Contains(entries, e => e.Raw == "/usr/share/common"
                                      && e.Access == SandboxAccess.ReadOnly);

        // Platform-specific entries:
        if (OperatingSystem.IsMacOS())
        {
            Assert.Contains(entries, e => e.Raw == "/mac/specific/path");
            Assert.DoesNotContain(entries, e => e.Raw == "/linux/specific/path");
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.Contains(entries, e => e.Raw == "/linux/specific/path");
            Assert.DoesNotContain(entries, e => e.Raw == "/mac/specific/path");
        }
        // On Windows, neither platform-specific entry should appear (only cross-platform).
        if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain(entries, e => e.Raw == "/mac/specific/path");
            Assert.DoesNotContain(entries, e => e.Raw == "/linux/specific/path");
        }
    }

    [Fact]
    public void ParseGroupJson_ExpandedFieldIsPreserved()
    {
        var groupJson = SampleAllowGroupJson();
        const string groupName = "test_runtime";

        var entries = SandboxPathInspector.ParseGroupJson(groupJson, groupName);

        var writable = entries.First(e => e.Raw == "/tmp/runtime-cache");
        Assert.Equal("/private/tmp/runtime-cache", writable.Expanded);
    }

    // ── Unavailable / degraded state ─────────────────────────────────────
    [Fact]
    public async Task InspectAsync_ReturnsUnavailableWhenNonoAbsent()
    {
        // A path that definitely does not exist on the filesystem.
        var fakeBinary = Path.Combine(Path.GetTempPath(),
            "nono-does-not-exist-" + Guid.NewGuid().ToString("N"));

        var result = await SandboxPathInspector.InspectAsync(
            workspaceRoot: "/tmp/ws",
            nonoBinary: fakeBinary);

        Assert.False(result.IsAvailable);
        Assert.Empty(result.ReadablePaths);
        Assert.Empty(result.WritablePaths);
        Assert.Empty(result.BlockedPaths);
    }

    // ── Derived, not hardcoded (group variant) ───────────────────────────
    [Fact]
    public void PathAddedToGroupJson_AppearsInOutput()
    {
        var json = SampleAllowGroupJson();

        // The base JSON contains "/usr/local/bin" but not "/opt/new-tool/bin".
        var baseEntries = SandboxPathInspector.ParseGroupJson(json, "runtime");
        Assert.Contains(baseEntries, e => e.Raw == "/usr/local/bin");
        Assert.DoesNotContain(baseEntries, e => e.Raw == "/opt/new-tool/bin");

        // Patch in the new path as an allow.read entry.
        var patched = PatchGroupAllowRead(json, "/opt/new-tool/bin", "/opt/new-tool/bin");
        var patchedEntries = SandboxPathInspector.ParseGroupJson(patched, "runtime");

        Assert.Contains(patchedEntries,
            e => e.Raw == "/opt/new-tool/bin"
                 && e.Access == SandboxAccess.ReadOnly
                 && e.Source == "runtime");
    }
}
