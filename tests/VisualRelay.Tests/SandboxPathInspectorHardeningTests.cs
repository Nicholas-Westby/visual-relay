using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Hardening tests (task 11 code review): InspectAsync must NEVER throw for a nono
/// that exits 0 with unexpected/malformed output (VR resolves <c>nono</c> by bare
/// name, so a foreign or older binary is possible) — it must degrade to Unavailable.
/// Also locks the resolved-chain <c>exclude</c> handling, the null-path guard, and
/// consistent platform-token filtering. Partial of <see cref="SandboxPathInspectorTests"/>.
/// </summary>
public sealed partial class SandboxPathInspectorTests
{
    // ── Never-throw on malformed nono output (degrade, not crash) ─────────
    [Fact]
    public async Task ExpandInheritedGroupsAsync_MalformedShowJson_ReturnsNullNotThrow()
    {
        // `nono profile show` exited 0 but printed non-JSON (e.g. a banner / usage).
        var entries = await SandboxPathInspector.ExpandInheritedGroupsAsync(
            "not json at all {{{", _ => Task.FromResult<string?>(null));

        Assert.Null(entries);
    }

    [Fact]
    public async Task ExpandInheritedGroupsAsync_MalformedGroupJson_ReturnsNullNotThrow()
    {
        var show = """{ "groups": { "include": ["bad_group"] } }""";

        // A group query exits 0 but returns garbage — must degrade, not throw.
        var entries = await SandboxPathInspector.ExpandInheritedGroupsAsync(
            show, _ => Task.FromResult<string?>("<<< not json >>>"));

        Assert.Null(entries);
    }

    [Fact]
    public async Task ExpandInheritedGroupsAsync_UnexpectedShapeShowJson_ReturnsNullNotThrow()
    {
        // Well-formed JSON but the wrong shape (groups.include is not an array).
        var show = """{ "groups": { "include": "oops" } }""";

        var entries = await SandboxPathInspector.ExpandInheritedGroupsAsync(
            show, _ => Task.FromResult<string?>(null));

        Assert.Null(entries);
    }

    // ── Resolved chain honours groups.exclude ────────────────────────────
    [Fact]
    public void ParseResolvedGroupIncludes_SubtractsExcludedGroups()
    {
        // If the resolved chain reports an excluded group, it must NOT be expanded —
        // expanding it would display a denial/grant that is not actually enforced.
        var show = """
            { "groups": { "include": ["deny_credentials", "git_config"],
                          "exclude": ["git_config"] } }
            """;

        var names = SandboxPathInspector.ParseResolvedGroupIncludes(show);

        Assert.Contains("deny_credentials", names);
        Assert.DoesNotContain("git_config", names);
    }

    // ── Null path values are skipped, not surfaced as blank rows ──────────
    [Fact]
    public void ParseGroupJson_SkipsNullRawDenyEntry()
    {
        var groupJson =
            """{ "name":"g", "deny": { "access": [ {"raw":null,"expanded":null}, {"raw":"~/.ssh"} ] } }""";

        var entries = SandboxPathInspector.ParseGroupJson(groupJson, "g");

        Assert.DoesNotContain(entries, e => string.IsNullOrEmpty(e.Raw));
        Assert.Contains(entries, e => e.Raw == "~/.ssh");
    }

    // ── Platform-token filtering is consistent (group-level vs per-entry) ─
    [Fact]
    public void ParseGroupJson_NonCanonicalPlatformEntry_IsIncludedConsistently()
    {
        // A non-canonical platform token must be handled the same way at the entry
        // level as at the group level (included, not silently dropped on every OS).
        var groupJson =
            """{ "allow": { "read": [ {"raw":"/x","expanded":"/x","platform":"future-os"} ] } }""";

        var entries = SandboxPathInspector.ParseGroupJson(groupJson, "g");

        Assert.Contains(entries, e => e.Raw == "/x" && e.Access == SandboxAccess.ReadOnly);
    }

    [Fact]
    public async Task ExpandInheritedGroupsAsync_NonCanonicalPlatformGroup_IsIncluded()
    {
        var show = """{ "groups": { "include": ["odd"] } }""";

        var entries = await SandboxPathInspector.ExpandInheritedGroupsAsync(
            show, _ => Task.FromResult<string?>(
                """{ "name":"odd", "platform":"future-os", "deny": { "access": [ {"raw":"~/.odd"} ] } }"""));

        Assert.NotNull(entries);
        Assert.Contains(entries!, e => e.Raw == "~/.odd" && e.Access == SandboxAccess.Blocked);
    }
}
