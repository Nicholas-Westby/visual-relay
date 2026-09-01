using System.Text;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

/// <summary>
/// Non-ASCII path rows of the Tier A matrix: CJK, emoji, right-to-left script,
/// spaces, and the NFD-versus-NFC normalization question that only bites on a
/// normalization-insensitive file system.
/// </summary>
public sealed partial class TargetRepoMatrixTierATests
{
    /// <summary>"cafe" + COMBINING ACUTE ACCENT — the decomposed (NFD) spelling.</summary>
    private const string DecomposedName = "cafe\u0301";

    /// <summary>"café" as a single precomposed code point — the NFC spelling.</summary>
    private const string ComposedName = "caf\u00e9";

    /// <summary>
    /// Detection is byte-for-byte path handling with no normalization or
    /// encoding assumptions, so every script, emoji, direction and embedded
    /// space works as long as the probe uses the same string the directory was
    /// created with.
    /// </summary>
    /// <param name="directoryName">The non-ASCII tail of the repo directory name.</param>
    [Theory]
    [InlineData("日本語のプロジェクト")]
    [InlineData("проект")]
    [InlineData("rocket-🚀-repo")]
    [InlineData("مشروع-عربي")]
    [InlineData("repo with spaces")]
    [InlineData(ComposedName)]
    [InlineData(DecomposedName)]
    public void Row_NonAsciiRepoPath_DetectsNormally(string directoryName)
    {
        var root = NewRepo(directoryName);
        try
        {
            Write(root, "go.mod", "module example.com/m\n");
            Write(root, "main_test.go", "package main\n");

            Assert.Equal(["go test ./..."], TestCommandDetector.DetectCandidates(root));
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    /// <summary>
    /// The written config survives a non-ASCII root: the file lands inside the
    /// oddly-named directory and reloads to the same command.
    /// </summary>
    [Fact]
    public async Task Row_NonAsciiRepoPath_WritesAndReloadsItsConfig()
    {
        var root = NewRepo("設定-🚀-repo");
        try
        {
            Write(root, "Cargo.toml", "[package]\nname = \"sample\"\n");

            var (result, config) = await BootstrapAsync(root);

            Assert.Equal("cargo test", result.TestCommand);
            Assert.StartsWith(root, result.ConfigPath, StringComparison.Ordinal);
            Assert.Contains("\"testCmd\": \"cargo test\"", config, StringComparison.Ordinal);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    /// <summary>
    /// NFD versus NFC: the two spellings are DIFFERENT strings, and .NET does
    /// no normalization of its own. On macOS's normalization-insensitive file
    /// system a directory created decomposed is still found through its
    /// composed path, so detection succeeds across the forms. On a
    /// normalization-sensitive file system (ext4) the same probe would find
    /// nothing — which is why the two spellings must never be compared as
    /// strings when deciding whether two repo paths are the same repo.
    /// </summary>
    [Fact]
    public void Row_NfdDirectoryProbedWithNfcPath_ResolvesOnMacOs()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "normalization-insensitive file system");
        Assert.NotEqual(DecomposedName, ComposedName);
        Assert.Equal(ComposedName, DecomposedName.Normalize(NormalizationForm.FormC));

        var root = NewRepo(DecomposedName);
        try
        {
            Write(root, "Cargo.toml", "[package]\nname = \"sample\"\n");

            var composedRoot = root.Normalize(NormalizationForm.FormC);
            Assert.NotEqual(root, composedRoot);

            Assert.Equal(["cargo test"], TestCommandDetector.DetectCandidates(root));
            Assert.Equal(["cargo test"], TestCommandDetector.DetectCandidates(composedRoot));
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    /// <summary>
    /// Test-path classification is code-point based too: a non-ASCII file name
    /// still matches the <c>_test</c> suffix rule, and a non-ASCII directory
    /// that is not a recognized test-directory name is correctly not matched.
    /// </summary>
    [Fact]
    public void Row_NonAsciiTestFileNames_ClassifyOnTheSameRules()
    {
        Assert.True(TestPathClassifier.IsRunnableTestFile("テスト/日本語_test.go", null));
        Assert.True(TestPathClassifier.IsRunnableTestFile("src/tests/مشروع.test.ts", null));
        Assert.False(TestPathClassifier.IsTestRelated("テスト/日本語.go", null));
    }
}
