using System.Text.Json;

namespace VisualRelay.Tests;

/// <summary>
/// Guards the shipped vr-guard nono profile's rollback exclusions. nono's
/// rollback PREFLIGHT snapshots EVERY writable path, and the profile grants
/// write to many large, regenerable toolchain caches (.nuget, .cache, .bun, …).
/// Those caches must be excluded from snapshots or they blow nono's fixed
/// ~2 GiB rollback budget and kill every swival coding run on a real machine
/// (observed: "Rollback budget exceeded" before swival did any work). This
/// asserts the profile keeps those cache exclusions plus the base node_modules
/// exclusion — the workspace's own large git-ignored dirs are handled
/// separately/dynamically by <see cref="VisualRelay.Core.Execution.NonoRollbackSkipDirs"/>.
/// </summary>
public sealed class VrGuardProfileRollbackTests
{
    private static string ProfilePath =>
        Path.Combine(RepoSetup.Root, "packaging", "nono", "vr-guard.json");

    [Fact]
    public void VrGuardProfile_ExcludesLargeToolchainCaches_FromRollback()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ProfilePath));
        var patterns = doc.RootElement
            .GetProperty("rollback")
            .GetProperty("exclude_patterns")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToHashSet(StringComparer.Ordinal);

        // The big writable caches the profile itself grants write to — none of
        // these should ever be snapshotted for rollback (regenerable, and they
        // exceed the budget). Component-name matches.
        foreach (var required in new[] { ".nuget", ".cache", "Caches", ".bun", ".npm", ".dotnet", ".cargo", "Unity" })
        {
            Assert.Contains(required, patterns);
        }

        // The base swival profile excludes node_modules; it must survive the
        // vr-guard override (whether nono merges or replaces the rollback block).
        Assert.Contains("node_modules", patterns);
    }
}
