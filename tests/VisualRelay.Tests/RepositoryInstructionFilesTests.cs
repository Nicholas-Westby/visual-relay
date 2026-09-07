using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Covers <see cref="RepositoryInstructionFiles.Find"/>: the fixed, in-code list
/// of well-known contributor/agent instruction files and the order candidates
/// are reported in when a repository ships them.
/// </summary>
public sealed class RepositoryInstructionFilesTests
{
    [Fact]
    public void EmptyRepo_ReturnsEmpty()
    {
        using var dir = new TempDirectory();

        var result = RepositoryInstructionFiles.Find(dir.Path);

        Assert.Empty(result);
    }

    [Fact]
    public void NonExistentRoot_ReturnsEmpty()
    {
        var missing = Path.Combine(Path.GetTempPath(), "vr-does-not-exist", Guid.NewGuid().ToString("N"));

        var result = RepositoryInstructionFiles.Find(missing);

        Assert.Empty(result);
    }

    [Fact]
    public void SeveralCandidates_ReturnedInRequiredOrder()
    {
        using var dir = new TempDirectory();
        // Written out of on-disk / alphabetical order to prove Find sorts by
        // priority, not discovery order.
        File.WriteAllText(Path.Combine(dir.Path, ".windsurfrules"), "w");
        File.WriteAllText(Path.Combine(dir.Path, "AGENTS.md"), "a");
        Directory.CreateDirectory(Path.Combine(dir.Path, ".github"));
        File.WriteAllText(Path.Combine(dir.Path, ".github", "CONTRIBUTING.md"), "g");
        File.WriteAllText(Path.Combine(dir.Path, "CONTRIBUTING.md"), "c");
        File.WriteAllText(Path.Combine(dir.Path, ".cursorrules"), "cr");

        var result = RepositoryInstructionFiles.Find(dir.Path);

        Assert.Equal(
            new[] { "AGENTS.md", "CONTRIBUTING.md", ".github/CONTRIBUTING.md", ".cursorrules", ".windsurfrules" },
            result);
    }

    [Fact]
    public void CursorRulesDirectory_EmptyIsExcluded_WithAFileIsIncluded()
    {
        using var dir = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(dir.Path, ".cursor", "rules"));

        Assert.DoesNotContain(".cursor/rules", RepositoryInstructionFiles.Find(dir.Path));

        File.WriteAllText(Path.Combine(dir.Path, ".cursor", "rules", "style.mdc"), "r");

        Assert.Contains(".cursor/rules", RepositoryInstructionFiles.Find(dir.Path));
    }
}
