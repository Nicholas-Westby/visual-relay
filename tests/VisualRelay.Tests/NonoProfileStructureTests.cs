using System.Text.Json;

namespace VisualRelay.Tests;

/// <summary>
/// Structural assertions on <c>packaging/nono/vr-guard.json</c>. These run in
/// the default <c>dotnet test</c> suite (no nono shell-out) and validate the
/// profile is valid JSON, extends swival, has the required toolchain-cache
/// <c>filesystem.allow</c> entries, and uses <c>$HOME</c>/<c>when</c> predicates
/// (no hardcoded <c>/Users/</c> paths).
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

    [Fact]
    public void VrGuardProfile_ExtendsSwival()
    {
        var profilePath = ResolveProfilePath();
        using var doc = JsonDocument.Parse(File.ReadAllText(profilePath));

        Assert.True(doc.RootElement.TryGetProperty("extends", out var extends));
        Assert.Equal("swival", extends.GetString());
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

    internal static string ResolveProfilePath()
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

    internal static IReadOnlyList<string> CollectPaths(JsonElement entries)
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
