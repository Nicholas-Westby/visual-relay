using System.Text.Json;

namespace VisualRelay.Tests;

/// <summary>
/// Structural assertions on <c>packaging/nono/vr-guard.json</c>. These run in
/// the default <c>dotnet test</c> suite (no nono shell-out) and validate the
/// profile is valid JSON, is self-contained on top of nono's built-in
/// <c>default</c>, has the required toolchain-cache <c>filesystem.allow</c>
/// entries, and uses <c>$HOME</c>/<c>when</c> predicates (no hardcoded
/// <c>/Users/</c> paths).
/// </summary>
public sealed class NonoProfileStructureTests
{
    [Fact]
    public void VrGuardProfile_IsValidJson()
    {
        var profilePath = ResolveProfilePath();
        Assert.True(File.Exists(profilePath), $"vr-guard.json not found at {profilePath}");

        var json = File.ReadAllText(profilePath);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    /// <summary>
    /// The profile inherits from nono's own built-in base and nothing else.
    /// It used to extend <c>swival</c> — a third-party pack the launcher had to
    /// <c>nono pull</c> on every start — for an agent Visual Relay no longer runs.
    /// Everything that pack contributed is now declared here, so the sandbox does
    /// not depend on someone else's release cadence.
    /// </summary>
    [Fact]
    public void VrGuardProfile_ExtendsTheBuiltInDefault_NotAThirdPartyPack()
    {
        var profilePath = ResolveProfilePath();
        using var doc = JsonDocument.Parse(File.ReadAllText(profilePath));

        Assert.True(doc.RootElement.TryGetProperty("extends", out var extends));
        Assert.Equal("default", extends.GetString());
    }

    /// <summary>
    /// The seven groups the swival layer used to add on top of <c>default</c>.
    /// Dropping the inheritance without re-declaring these would silently strip
    /// unlink protection, git config access and the language-runtime caches.
    /// </summary>
    [Theory]
    [InlineData("python_runtime")]
    [InlineData("node_runtime")]
    [InlineData("user_caches_macos")]
    [InlineData("user_caches_linux")]
    [InlineData("linux_sysfs_read")]
    [InlineData("git_config")]
    [InlineData("unlink_protection")]
    public void VrGuardProfile_DeclaresEveryGroupItUsedToInherit(string group)
    {
        var profilePath = ResolveProfilePath();
        using var doc = JsonDocument.Parse(File.ReadAllText(profilePath));

        var included = doc.RootElement
            .GetProperty("groups").GetProperty("include")
            .EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Contains(group, included);
    }

    /// <summary>
    /// <c>default</c> grants no workspace access at all (<c>workdir.access</c> is
    /// <c>none</c>) and leaves <c>capability_elevation</c> unset. Both came from
    /// the swival layer, so the profile has to state them itself — without the
    /// workdir grant every stage would be denied writes to its own checkout.
    /// </summary>
    [Fact]
    public void VrGuardProfile_StatesTheWorkdirAndSecuritySettingsDefaultOmits()
    {
        var profilePath = ResolveProfilePath();
        using var doc = JsonDocument.Parse(File.ReadAllText(profilePath));
        var root = doc.RootElement;

        Assert.Equal("readwrite",
            root.GetProperty("workdir").GetProperty("access").GetString());
        Assert.False(
            root.GetProperty("security").GetProperty("capability_elevation").GetBoolean());
        Assert.Equal("isolated",
            root.GetProperty("security").GetProperty("signal_mode").GetString());
        Assert.False(root.GetProperty("network").GetProperty("block").GetBoolean());
    }

    /// <summary>
    /// Nothing in the profile may name swival again: no inherited pack, no grant
    /// for its config dirs, no rollback exclusion for its scratch directory.
    /// </summary>
    [Fact]
    public void VrGuardProfile_NamesSwivalNowhere()
    {
        var profilePath = ResolveProfilePath();

        var json = File.ReadAllText(profilePath);

        Assert.DoesNotContain("swival", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VrGuardProfile_HasFilesystemAllowEntries()
    {
        // FAILS today — profile has only filesystem.read:["/"], no allow.
        var profilePath = ResolveProfilePath();
        using var doc = JsonDocument.Parse(File.ReadAllText(profilePath));
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("filesystem", out var fs));
        Assert.True(fs.TryGetProperty("allow", out var allow));
        Assert.Equal(JsonValueKind.Array, allow.ValueKind);
        Assert.NotEmpty(allow.EnumerateArray());

        // Every entry must use variable expansion — no hardcoded /Users/.
        foreach (var entry in allow.EnumerateArray())
        {
            var path = entry.ValueKind == JsonValueKind.String
                ? entry.GetString()!
                : (entry.TryGetProperty("path", out var p) ? p.GetString()! : "");
            Assert.False(path.StartsWith("/Users/", StringComparison.Ordinal),
                $"Entry '{path}' must not hardcode /Users/; use $HOME instead.");
        }
    }

    [Fact]
    public void VrGuardProfile_HasDotNetEntries()
    {
        var profilePath = ResolveProfilePath();
        using var doc = JsonDocument.Parse(File.ReadAllText(profilePath));

        Assert.True(doc.RootElement.TryGetProperty("filesystem", out var fs));
        Assert.True(fs.TryGetProperty("allow", out var allow));
        var paths = CollectPaths(allow);
        Assert.Contains(paths, p => p.Contains(".nuget"));
        Assert.Contains(paths, p => p.Contains(".dotnet"));
    }

    [Fact]
    public void VrGuardProfile_HasSwiftEntries()
    {
        var profilePath = ResolveProfilePath();
        using var doc = JsonDocument.Parse(File.ReadAllText(profilePath));

        Assert.True(doc.RootElement.TryGetProperty("filesystem", out var fs));
        Assert.True(fs.TryGetProperty("allow", out var allow));
        var paths = CollectPaths(allow);
        Assert.Contains(paths, p => p.Contains(".swiftpm"));
    }

    [Fact]
    public void VrGuardProfile_HasUnityEntries()
    {
        var profilePath = ResolveProfilePath();
        using var doc = JsonDocument.Parse(File.ReadAllText(profilePath));

        Assert.True(doc.RootElement.TryGetProperty("filesystem", out var fs));
        Assert.True(fs.TryGetProperty("allow", out var allow));
        var paths = CollectPaths(allow);
        Assert.Contains(paths, p => p == "$HOME/Library/Unity");
        Assert.Contains(paths, p => p == "$HOME/Library/Application Support/Unity");
        Assert.Contains(paths, p => p == "$HOME/Library/Application Support/UnityHub");
    }

    [Fact]
    public void VrGuardProfile_GrantsCargoHome_NotJustRegistryAndGit()
    {
        // Cargo writes lock/state files (.package-cache, .package-cache-mutate,
        // .global-cache, config) directly under $HOME/.cargo — NOT only the
        // registry/git sub-dirs. Granting only $HOME/.cargo/registry and
        // $HOME/.cargo/git left those writes denied, so cargo fell back to a
        // workspace-local CARGO_HOME and vendored the entire crates.io cache
        // (63 MB / 9,289 files) into the committed project. The profile must
        // grant the cargo HOME itself so cargo stays at its default location.
        var profilePath = ResolveProfilePath();
        using var doc = JsonDocument.Parse(File.ReadAllText(profilePath));

        Assert.True(doc.RootElement.TryGetProperty("filesystem", out var fs));
        Assert.True(fs.TryGetProperty("allow", out var allow));
        var paths = CollectPaths(allow);
        Assert.Contains("$HOME/.cargo", paths);
    }

    [Fact]
    public void VrGuardProfile_HasNixEntries()
    {
        // nix-managed target projects install deps in-sandbox via the daemon;
        // the blocker was the nix cache/state writes (~/.cache/nix readonly-db
        // + lock files), NOT the daemon socket and NOT a writable /nix/store.
        var profilePath = ResolveProfilePath();
        using var doc = JsonDocument.Parse(File.ReadAllText(profilePath));

        Assert.True(doc.RootElement.TryGetProperty("filesystem", out var fs));
        Assert.True(fs.TryGetProperty("allow", out var allow));
        var paths = CollectPaths(allow);
        Assert.Contains(paths, p => p == "$HOME/.cache/nix");
        Assert.Contains(paths, p => p == "$HOME/.local/state/nix");
    }

    [Fact]
    public void VrGuardProfile_HasWhenPredicatesForOsSpecificPaths()
    {
        var profilePath = ResolveProfilePath();
        using var doc = JsonDocument.Parse(File.ReadAllText(profilePath));

        Assert.True(doc.RootElement.TryGetProperty("filesystem", out var fs));
        Assert.True(fs.TryGetProperty("allow", out var allow));

        var hasWhen = allow.EnumerateArray().Any(
            e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("when", out _));
        Assert.True(hasWhen,
            "vr-guard.json must use 'when' predicates for OS-specific paths");
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static string ResolveProfilePath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var p = Path.Combine(dir, "packaging", "nono", "vr-guard.json");
            if (File.Exists(p)) return p;
            if (File.Exists(Path.Combine(dir, "visual-relay.slnx"))
                || Directory.Exists(Path.Combine(dir, ".git")))
            {
                // We're at the repo root but packaging/nono/vr-guard.json
                // lives under the repo root.
                p = Path.Combine(dir, "packaging", "nono", "vr-guard.json");
                if (File.Exists(p)) return p;
            }
            dir = Path.GetDirectoryName(dir);
        }
        // Fallback: installed location.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return Path.Combine(xdg ?? Path.Combine(home, ".config"),
            "nono", "profiles", "vr-guard.json");
    }

    private static IReadOnlyList<string> CollectPaths(JsonElement entries)
    {
        var paths = new List<string>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
                paths.Add(entry.GetString()!);
            else if (entry.ValueKind == JsonValueKind.Object
                     && entry.TryGetProperty("path", out var p))
                paths.Add(p.GetString()!);
        }
        return paths;
    }
}
