using System.Text.Json;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;
/// <summary>
/// Tests for <see cref="SandboxPathInspector"/> — proves the classifier is derived
/// from runtime input (profile JSON + nono group output), never hardcoded.
/// </summary>
public sealed partial class SandboxPathInspectorTests
{
    // ── Own-directives parsing (vr-guard JSON) ───────────────────────────
    [Fact]
    public void ParseOwnDirectives_ClassifiesAllowReadDeny()
    {
        var json = SampleVrGuardJson();

        var entries = SandboxPathInspector.ParseOwnDirectives(json);

        // vr-guard's own allow entries → ReadWrite
        var writable = entries.Where(e => e.Access == SandboxAccess.ReadWrite).ToList();
        Assert.NotEmpty(writable);
        Assert.Contains(writable, e => e.Raw == "$HOME/.nuget/packages" && e.Source == "vr-guard");
        Assert.Contains(writable, e => e.Raw == "$HOME/.cargo" && e.Source == "vr-guard");

        // vr-guard's own read entries → ReadOnly
        var readable = entries.Where(e => e.Access == SandboxAccess.ReadOnly).ToList();
        Assert.NotEmpty(readable);
        Assert.Contains(readable, e => e.Raw == "/" && e.Source == "vr-guard");
        Assert.Contains(readable, e => e.Raw == "$HOME/.gitconfig" && e.Source == "vr-guard");

        // vr-guard's own deny entries → Blocked
        var blocked = entries.Where(e => e.Access == SandboxAccess.Blocked).ToList();
        Assert.NotEmpty(blocked);
        Assert.Contains(blocked, e => e.Raw == "$HOME/Documents" && e.Source == "vr-guard");
        Assert.Contains(blocked, e => e.Raw == "$HOME/Desktop" && e.Source == "vr-guard");
    }

    [Fact]
    public void ParseOwnDirectives_FiltersByOsWhenPredicate()
    {
        var json = SampleVrGuardJson();

        var entries = SandboxPathInspector.ParseOwnDirectives(json);

        // "when":"macos" entry should be included on macOS, excluded on Linux.
        if (OperatingSystem.IsMacOS())
        {
            Assert.Contains(entries, e => e.Raw == "$HOME/Library/Caches/NuGet"
                                          && e.Access == SandboxAccess.ReadWrite);
            Assert.DoesNotContain(entries, e => e.Raw == "$XDG_CACHE_HOME/NuGet");
        }

        // "when":"linux" entry should be excluded on macOS, included on Linux.
        if (OperatingSystem.IsLinux())
        {
            Assert.Contains(entries, e => e.Raw == "$XDG_CACHE_HOME/NuGet"
                                          && e.Access == SandboxAccess.ReadWrite);
            Assert.DoesNotContain(entries, e => e.Raw == "$HOME/Library/Caches/NuGet");
        }

        // Entries WITHOUT a "when" predicate are always included on any OS.
        Assert.Contains(entries, e => e.Raw == "$HOME/.npm"
                                      && e.Access == SandboxAccess.ReadWrite);
        Assert.Contains(entries, e => e.Raw == "$HOME/.bun"
                                      && e.Access == SandboxAccess.ReadWrite);
    }

    [Fact]
    public void ParseOwnDirectives_AllSourceIsVrGuard()
    {
        var json = SampleVrGuardJson();

        var entries = SandboxPathInspector.ParseOwnDirectives(json);

        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.Equal("vr-guard", e.Source));
    }

    // ── Derived, not hardcoded ───────────────────────────────────────────
    [Fact]
    public void PathAddedToInput_AppearsInOutput()
    {
        // Start with a base json that lacks a unique marker path.
        var baseJson = SampleVrGuardJson();
        var baseEntries = SandboxPathInspector.ParseOwnDirectives(baseJson);
        Assert.DoesNotContain(baseEntries, e => e.Raw == "$HOME/.acme-toolchain/cache");

        // Now add that unique path to the allow array.
        var patchedJson = PatchAllowArray(baseJson, "$HOME/.acme-toolchain/cache");
        var patchedEntries = SandboxPathInspector.ParseOwnDirectives(patchedJson);

        // The added path must appear as ReadWrite with source "vr-guard".
        Assert.Contains(patchedEntries,
            e => e.Raw == "$HOME/.acme-toolchain/cache"
                 && e.Access == SandboxAccess.ReadWrite
                 && e.Source == "vr-guard");
    }

    // ── EmbeddedContent usage ────────────────────────────────────────────
    [Fact]
    public void EmbeddedContent_IsValidVrGuardJson()
    {
        // Prove that NonoProfileEnsurer.EmbeddedContent is the actual vr-guard
        // profile JSON (the single source of truth for own directives).
        var content = NonoProfileEnsurer.EmbeddedContent;

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("extends", out var extends));
        Assert.Equal("swival", extends.GetString());

        Assert.True(root.TryGetProperty("filesystem", out _));
        Assert.True(root.TryGetProperty("groups", out _));
    }

    [Fact]
    public void ParseOwnDirectives_AcceptsEmbeddedContentFormat()
    {
        // The ParseOwnDirectives method must accept the real embedded content
        // (not a different format). This encodes the stale-copy pitfall fix:
        // own directives come from EmbeddedContent, not ~/.config/nono/profiles/.
        var content = NonoProfileEnsurer.EmbeddedContent;

        var entries = SandboxPathInspector.ParseOwnDirectives(content);

        // The real embedded profile has allow, read, and deny sections.
        Assert.NotEmpty(entries);
        Assert.Contains(entries, e => e.Access == SandboxAccess.ReadWrite);
        Assert.Contains(entries, e => e.Access == SandboxAccess.ReadOnly);
        Assert.Contains(entries, e => e.Access == SandboxAccess.Blocked);

        // Verify specific known entries from the real vr-guard.json.
        Assert.Contains(entries, e => e.Raw == "/" && e.Access == SandboxAccess.ReadOnly);
        Assert.Contains(entries, e => e.Raw == "$HOME/.gitconfig" && e.Access == SandboxAccess.ReadOnly);
        Assert.Contains(entries, e => e.Raw == "$HOME/Documents" && e.Access == SandboxAccess.Blocked);
        Assert.Contains(entries, e => e.Raw == "$HOME/.cargo" && e.Access == SandboxAccess.ReadWrite);
        Assert.Contains(entries, e => e.Raw == "$HOME/.npm" && e.Access == SandboxAccess.ReadWrite);
    }

    // ── ExpandPath ───────────────────────────────────────────────────────
    [Fact]
    public void ExpandPath_ResolvesHomePrefix()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var expanded = SandboxPathInspector.ExpandPath("$HOME/Documents");

        Assert.Equal(Path.Combine(home, "Documents"), expanded);
    }

    [Fact]
    public void ExpandPath_ResolvesTildePrefix()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var expanded = SandboxPathInspector.ExpandPath("~/Documents");

        // ~ should resolve the same way as $HOME.
        Assert.Equal(Path.Combine(home, "Documents"), expanded);
    }

    [Fact]
    public void ExpandPath_ReturnsUnchangedWhenNoHomePrefix()
    {
        Assert.Equal("/usr/local/bin", SandboxPathInspector.ExpandPath("/usr/local/bin"));
        Assert.Equal("/tmp/scratch", SandboxPathInspector.ExpandPath("/tmp/scratch"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────
    /// <summary>
    /// A minimal vr-guard-style JSON with own allow/read/deny entries and
    /// OS "when" predicates for testing the classifier.
    /// </summary>
    private static string SampleVrGuardJson() =>
        """
        {
          "extends": "swival",
          "filesystem": {
            "read": ["/", "$HOME/.gitconfig"],
            "allow": [
              "$HOME/.nuget/packages",
              "$HOME/.cargo",
              { "path": "$HOME/Library/Caches/NuGet", "when": "macos" },
              { "path": "$XDG_CACHE_HOME/NuGet", "when": "linux" },
              "$HOME/.npm",
              "$HOME/.bun"
            ],
            "deny": [
              "$HOME/Documents",
              "$HOME/Desktop"
            ]
          },
          "groups": {
            "include": ["test_runtime"]
          }
        }
        """;

    /// <summary>
    /// A sample nono allow-group JSON (mimicking <c>nono profile groups test_runtime --json</c>)
    /// with allow.read, allow.readwrite, and deny.access entries.
    /// </summary>
    private static string SampleAllowGroupJson() =>
        """
        {
          "allow": {
            "read": [
              { "raw": "/usr/local/bin", "expanded": "/usr/local/bin", "platform": "cross-platform" }
            ],
            "readwrite": [
              { "raw": "/tmp/runtime-cache", "expanded": "/private/tmp/runtime-cache", "platform": "cross-platform" }
            ]
          },
          "deny": {
            "access": ["/etc/secret"]
          }
        }
        """;

    /// <summary>
    /// A sample nono deny-group JSON that includes deny.commands and deny.unlink
    /// entries that must be ignored (they are not filesystem paths).
    /// </summary>
    private static string SampleDenyGroupJson() =>
        """
        {
          "allow": {},
          "deny": {
            "access": ["/etc/secret", "/root/.ssh"],
            "commands": ["curl", "wget"],
            "unlink": ["/var/run/important.pid"]
          }
        }
        """;

    /// <summary>
    /// A sample group JSON with platform-specific entries to test OS filtering.
    /// </summary>
    private static string SampleCrossPlatformGroupJson() =>
        """
        {
          "allow": {
            "read": [
              { "raw": "/usr/share/common", "expanded": "/usr/share/common", "platform": "cross-platform" },
              { "raw": "/mac/specific/path", "expanded": "/mac/specific/path", "platform": "macos" },
              { "raw": "/linux/specific/path", "expanded": "/linux/specific/path", "platform": "linux" }
            ]
          }
        }
        """;

    /// <summary>
    /// Patches an extra path string into the filesystem.allow array of a vr-guard JSON.
    /// </summary>
    private static string PatchAllowArray(string json, string newPath)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var fs = root.GetProperty("filesystem");
        var allow = fs.GetProperty("allow");

        var list = new List<string>();
        foreach (var entry in allow.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
                list.Add(entry.GetString()!);
            else if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("path", out var p))
                list.Add(p.GetString()!);
        }
        list.Add(newPath);

        var newAllow = string.Join(",", list.Select(p => $"\"{p}\""));
        var oldAllow = allow.GetRawText();
        return json.Replace(oldAllow, $"[{newAllow}]");
    }

    /// <summary>
    /// Patches a new allow.read entry into a group JSON payload.
    /// </summary>
    private static string PatchGroupAllowRead(string json, string raw, string expanded)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var allow = root.GetProperty("allow");
        var read = allow.GetProperty("read");

        // Build the new read array with the extra entry.
        var entries = new List<string>();
        foreach (var entry in read.EnumerateArray())
            entries.Add(entry.GetRawText());
        entries.Add($$"""{"raw":"{{raw}}","expanded":"{{expanded}}","platform":"cross-platform"}""");

        var oldRead = read.GetRawText();
        var newRead = $"[{string.Join(",", entries)}]";
        return json.Replace(oldRead, newRead);
    }
}
