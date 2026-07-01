using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for the inherited-profile resolution added by task 11: the inspector must
/// resolve the whole <c>extends</c> chain (vr-guard → swival → default) via
/// <c>nono profile show --json</c>, then expand every included group — so the
/// enforced-but-previously-invisible credential denials (e.g. <c>~/.ssh</c> from
/// <c>deny_credentials</c>) surface in the Blocked list. Declared as a partial of
/// <see cref="SandboxPathInspectorTests"/> so shared helpers are reused. No test
/// shells out to real nono — group payloads are fed directly.
/// </summary>
public sealed partial class SandboxPathInspectorTests
{
    // ── Resolved groups.include parsing (nono profile show --json) ────────
    [Fact]
    public void ParseResolvedGroupIncludes_ExtractsEveryInheritedGroup()
    {
        var showJson = SampleResolvedShowJson();

        var names = SandboxPathInspector.ParseResolvedGroupIncludes(showJson);

        // The resolved chain surfaces inherited deny groups the own profile lacks.
        Assert.Contains("deny_credentials", names);
        Assert.Contains("deny_keychains_macos", names);
        Assert.Contains("deny_keychains_linux", names);
        Assert.Contains("git_config", names);
        // Own groups are present too (they are part of the resolved include list).
        Assert.Contains("go_runtime", names);
    }

    // ── The headline gap: credential denials must reach Blocked ──────────
    [Fact]
    public async Task ExpandInheritedGroupsAsync_CredentialDenialLandsInBlocked()
    {
        var showJson = SampleResolvedShowJson();

        var entries = await SandboxPathInspector.ExpandInheritedGroupsAsync(
            showJson, GroupPayloadProvider);

        Assert.NotNull(entries);
        // ~/.ssh (from deny_credentials) MUST appear as Blocked, attributed to its group.
        Assert.Contains(entries!, e => e.Raw == "~/.ssh"
                                       && e.Access == SandboxAccess.Blocked
                                       && e.Source == "deny_credentials");
        // The whole credentials set is derived, not just the one sample path.
        Assert.Contains(entries!, e => e.Raw == "~/.aws" && e.Access == SandboxAccess.Blocked);
        // The object-form "expanded" field is preserved for display.
        var ssh = entries!.First(e => e.Raw == "~/.ssh");
        Assert.EndsWith("/.ssh", ssh.Expanded);
    }

    // ── allow/read groups populate Readable + Writable ───────────────────
    [Fact]
    public async Task ExpandInheritedGroupsAsync_AllowGroupsPopulateReadableAndWritable()
    {
        var showJson = SampleResolvedShowJson();

        var entries = await SandboxPathInspector.ExpandInheritedGroupsAsync(
            showJson, GroupPayloadProvider);

        Assert.NotNull(entries);
        // git_config exposes ~/.gitconfig as a read grant.
        Assert.Contains(entries!, e => e.Raw == "~/.gitconfig"
                                       && e.Access == SandboxAccess.ReadOnly
                                       && e.Source == "git_config");
        // a readwrite cache grant lands in Writable.
        Assert.Contains(entries!, e => e.Raw == "~/.cache"
                                       && e.Access == SandboxAccess.ReadWrite);
    }

    // ── Per-group platform filter (deny_keychains_macos vs _linux) ───────
    [Fact]
    public async Task ExpandInheritedGroupsAsync_HonorsPerGroupPlatformFilter()
    {
        var showJson = SampleResolvedShowJson();

        var entries = await SandboxPathInspector.ExpandInheritedGroupsAsync(
            showJson, GroupPayloadProvider);

        Assert.NotNull(entries);
        if (OperatingSystem.IsMacOS())
        {
            Assert.Contains(entries!, e => e.Source == "deny_keychains_macos"
                                           && e.Raw == "~/Library/Keychains");
            Assert.DoesNotContain(entries!, e => e.Source == "deny_keychains_linux");
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.Contains(entries!, e => e.Source == "deny_keychains_linux"
                                           && e.Raw == "~/.local/share/keyrings");
            Assert.DoesNotContain(entries!, e => e.Source == "deny_keychains_macos");
        }
        else
        {
            // On any other OS neither platform-specific group contributes.
            Assert.DoesNotContain(entries!, e => e.Source == "deny_keychains_macos");
            Assert.DoesNotContain(entries!, e => e.Source == "deny_keychains_linux");
        }
    }

    // ── Graceful degradation: a failed group call → null (→ Unavailable) ──
    [Fact]
    public async Task ExpandInheritedGroupsAsync_ReturnsNullWhenAnyGroupFails()
    {
        var showJson = SampleResolvedShowJson();

        // Provider signals failure (null) for every group, mimicking a nono call
        // that exits non-zero — the inspector must degrade, not throw.
        var entries = await SandboxPathInspector.ExpandInheritedGroupsAsync(
            showJson, _ => Task.FromResult<string?>(null));

        Assert.Null(entries);
    }

    // ── Object-form deny.access (real nono shape) reaches Blocked ─────────
    [Fact]
    public void ParseGroupJson_HandlesObjectFormDenyAccess()
    {
        // Real `nono profile groups deny_credentials --json` emits deny.access as
        // objects {"raw":…,"expanded":…}, not bare strings. The parser must classify
        // them as Blocked and keep the expanded path.
        var entries = SandboxPathInspector.ParseGroupJson(
            SampleDenyCredentialsGroupJson(), "deny_credentials");

        var ssh = Assert.Single(entries, e => e.Raw == "~/.ssh");
        Assert.Equal(SandboxAccess.Blocked, ssh.Access);
        Assert.Equal("deny_credentials", ssh.Source);
        Assert.EndsWith("/.ssh", ssh.Expanded);
    }

    // ── Windows: honest read model (no phantom read-exceptions) ───────────
    [Fact]
    public void BuildWindowsResult_ReadableIsBroadWithNoClaimedExceptions()
    {
        var result = SandboxPathInspector.BuildWindowsResult("/ws", null);

        Assert.True(result.IsAvailable);
        // The Readable row must NOT imply macOS-style read-exceptions.
        Assert.All(result.ReadablePaths, e =>
        {
            Assert.DoesNotContain("except", e.Raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("blocked", e.Raw, StringComparison.OrdinalIgnoreCase);
        });
        // It must say reads are unrestricted.
        Assert.Contains(result.ReadablePaths,
            e => e.Raw.Contains("not restricted", StringComparison.OrdinalIgnoreCase));
        // Blocked conveys WRITE confinement only (correct for MXC).
        Assert.Contains(result.BlockedPaths,
            e => e.Raw.Contains("writes", StringComparison.OrdinalIgnoreCase));
        // The workspace remains a writable grant.
        Assert.Contains(result.WritablePaths, e => e.Raw == "/ws");
    }

    // ── Derived, not hardcoded (resolved-chain variant) ──────────────────
    [Fact]
    public async Task GroupAddedToResolvedChain_AppearsInOutput()
    {
        // A resolved include list that gains a brand-new group must flow through
        // with zero code change — proving derivation, not a snapshot.
        var showJson = """
            { "name":"vr-guard", "groups": { "include": ["acme_denies"], "exclude": [] } }
            """;

        var entries = await SandboxPathInspector.ExpandInheritedGroupsAsync(
            showJson,
            name => Task.FromResult<string?>(name == "acme_denies"
                ? """{ "name":"acme_denies", "platform":"cross-platform", "deny": { "access": [ {"raw":"~/.acme-secret","expanded":"/home/x/.acme-secret"} ] } }"""
                : null));

        Assert.NotNull(entries);
        Assert.Contains(entries!, e => e.Raw == "~/.acme-secret"
                                       && e.Access == SandboxAccess.Blocked
                                       && e.Source == "acme_denies");
    }

    // ── Fixtures modelled on real nono 0.61.1 output ─────────────────────

    /// <summary>
    /// Maps a group name to a sample <c>nono profile groups &lt;name&gt; --json</c>
    /// payload. Unknown groups return null (as a real missing group would).
    /// </summary>
    private static Task<string?> GroupPayloadProvider(string name) =>
        Task.FromResult<string?>(name switch
        {
            "deny_credentials" => SampleDenyCredentialsGroupJson(),
            "deny_keychains_macos" => SampleMacKeychainsGroupJson(),
            "deny_keychains_linux" => SampleLinuxKeychainsGroupJson(),
            "git_config" => SampleGitConfigGroupJson(),
            "user_caches" => SampleUserCachesGroupJson(),
            "go_runtime" => """{ "name":"go_runtime", "platform":"cross-platform" }""",
            _ => null,
        });

    /// <summary>
    /// A sample <c>nono profile show &lt;profile&gt; --json</c> result: the fully
    /// resolved chain whose groups.include carries every inherited group.
    /// </summary>
    private static string SampleResolvedShowJson() =>
        """
        {
          "name": "vr-guard",
          "extends": "swival",
          "groups": {
            "include": [
              "deny_credentials",
              "deny_keychains_macos",
              "deny_keychains_linux",
              "git_config",
              "user_caches",
              "go_runtime"
            ],
            "exclude": []
          }
        }
        """;

    /// <summary>Real-shaped deny_credentials payload (object-form deny.access).</summary>
    private static string SampleDenyCredentialsGroupJson() =>
        """
        {
          "name": "deny_credentials",
          "platform": "cross-platform",
          "deny": {
            "access": [
              { "raw": "~/.ssh", "expanded": "/Users/x/.ssh" },
              { "raw": "~/.aws", "expanded": "/Users/x/.aws" },
              { "raw": "~/.gnupg", "expanded": "/Users/x/.gnupg" }
            ]
          }
        }
        """;

    /// <summary>macOS-only keychains deny group (group-level platform=macos).</summary>
    private static string SampleMacKeychainsGroupJson() =>
        """
        {
          "name": "deny_keychains_macos",
          "platform": "macos",
          "deny": {
            "access": [
              { "raw": "~/Library/Keychains", "expanded": "/Users/x/Library/Keychains" }
            ]
          }
        }
        """;

    /// <summary>Linux-only keychains deny group (group-level platform=linux).</summary>
    private static string SampleLinuxKeychainsGroupJson() =>
        """
        {
          "name": "deny_keychains_linux",
          "platform": "linux",
          "deny": {
            "access": [
              { "raw": "~/.local/share/keyrings", "expanded": "/home/x/.local/share/keyrings" }
            ]
          }
        }
        """;

    /// <summary>Cross-platform allow.read group.</summary>
    private static string SampleGitConfigGroupJson() =>
        """
        {
          "name": "git_config",
          "platform": "cross-platform",
          "allow": {
            "read": [
              { "raw": "$HOME/.gitconfig", "expanded": "/Users/x/.gitconfig", "platform": "cross-platform" }
            ]
          }
        }
        """;

    /// <summary>Cross-platform allow.readwrite group.</summary>
    private static string SampleUserCachesGroupJson() =>
        """
        {
          "name": "user_caches",
          "platform": "cross-platform",
          "allow": {
            "readwrite": [
              { "raw": "~/.cache", "expanded": "/Users/x/.cache", "platform": "cross-platform" }
            ]
          }
        }
        """;
}
