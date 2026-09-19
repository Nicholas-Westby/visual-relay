using System.Text.RegularExpressions;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

public sealed partial class NonoReleaseTests
{
    [GeneratedRegex(@"pname = ""nono"";\s*version = ""(?<version>[^""]+)""")]
    private static partial Regex FlakeNonoVersion();

    /// <summary>
    /// Windows installs the release the Nix devshell builds, so a bump in one place that
    /// forgets the other fails here instead of leaving the two platforms on different nonos.
    /// </summary>
    [Fact]
    public void TheWindowsPin_IsTheReleaseTheNixDevshellBuilds()
    {
        var flake = File.ReadAllText(Path.Combine(RepoSetup.Root, "flake.nix"));

        var match = FlakeNonoVersion().Match(flake);

        Assert.True(match.Success, "flake.nix no longer builds nono as `pname = \"nono\"; version = \"...\"`");
        Assert.Equal(NonoRelease.Version, match.Groups["version"].Value);
    }
}
