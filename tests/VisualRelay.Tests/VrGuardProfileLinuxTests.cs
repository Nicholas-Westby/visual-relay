using System.Text.Json;

namespace VisualRelay.Tests;

/// <summary>
/// What nono accepts from vr-guard on Linux, the WSL distro included. Landlock is
/// allow-list only, so nono 0.75.0 refuses to start any sandbox whose grant is a
/// parent of its own state root or of a required deny (protected_paths.rs and
/// validate_deny_overlaps). Measured on WSL 2.7.14 with Ubuntu 26.04: a Linux
/// <c>read: "/"</c> made every launch fail with "Refusing to grant '/' (source:
/// Profile) because it overlaps protected nono state root".
/// </summary>
public sealed class VrGuardProfileLinuxTests
{
    private const string Home = "/home/u";

    // nono's state roots plus one path from each required deny group of its policy.
    private static readonly string[] MustStayUngranted =
    [
        $"{Home}/.local/state/nono", $"{Home}/.nono",
        $"{Home}/.ssh", $"{Home}/.gnupg", $"{Home}/.aws", $"{Home}/.config/gcloud",
        $"{Home}/.password-store", $"{Home}/.local/share/keyrings",
        $"{Home}/.config/google-chrome", $"{Home}/.mozilla/firefox",
        $"{Home}/.bash_history", $"{Home}/.bashrc", $"{Home}/.profile", $"{Home}/.config/fish",
    ];

    [Fact]
    public void LinuxGrants_CoverNoNonoStateRootAndNoRequiredDeny()
    {
        var offending =
            (from grant in LinuxGrants()
             from protectedPath in MustStayUngranted
             where protectedPath == grant || protectedPath.StartsWith(grant.TrimEnd('/') + "/", StringComparison.Ordinal)
             select $"{grant} covers {protectedPath}").ToList();

        Assert.True(offending.Count == 0, string.Join("\n", offending));
    }

    [Fact]
    public void LinuxGrants_StillReadTheSystemTheToolchainsLiveIn()
    {
        var grants = LinuxGrants().ToList();

        // Measured under nono in the distro with these grants: gcc, Python with
        // OpenSSL, curl and `go test` all ran.
        Assert.Contains("/usr", grants);
        Assert.Contains("/etc", grants);
        Assert.Contains("/opt", grants);
    }

    /// <summary>
    /// Maven and Gradle keep their dependency caches under the home directory, and a build
    /// writes them before it compiles. Measured in the WSL distro: apache/commons-lang's
    /// <c>mvn test</c> failed in 4 s with AccessDeniedException on
    /// <c>~/.m2/repository/.../_remote.repositories</c>, and passed once <c>~/.m2</c> was allowed.
    /// </summary>
    [Theory]
    [InlineData("$HOME/.m2")]
    [InlineData("$HOME/.gradle")]
    public void JavaBuildCaches_AreWritableOnEveryPlatform(string cache)
    {
        Assert.Contains(Entries("allow"), e => e.Path == cache && e.When is null);
    }

    [Fact]
    public void Macos_KeepsReadingTheWholeFilesystem()
    {
        Assert.Contains(Entries("read"), e => e.Path == "/" && e.When == "macos");
    }

    /// <summary>Every read, allow and write path that applies on Linux, with $HOME and ~ expanded.</summary>
    private static IEnumerable<string> LinuxGrants() =>
        Entries("read").Concat(Entries("allow")).Concat(Entries("write"))
            .Where(e => e.When is null || e.When.StartsWith("linux", StringComparison.Ordinal))
            .Select(e => e.Path.Replace("$XDG_CACHE_HOME", $"{Home}/.cache").Replace("$HOME", Home))
            .Select(p => p.StartsWith('~') ? Home + p[1..] : p)
            .Where(p => p.StartsWith('/'));

    private static IEnumerable<(string Path, string? When)> Entries(string key)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ProfilePath()));
        if (!doc.RootElement.GetProperty("filesystem").TryGetProperty(key, out var list))
            return [];
        return list.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String
            ? (e.GetString()!, null)
            : (e.GetProperty("path").GetString()!, e.TryGetProperty("when", out var w) ? w.GetString() : null)).ToList();
    }

    private static string ProfilePath() => Path.Combine(RepoSetup.Root, "packaging", "nono", "vr-guard.json");
}
