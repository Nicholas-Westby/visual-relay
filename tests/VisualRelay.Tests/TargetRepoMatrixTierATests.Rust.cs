using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

/// <summary>
/// Rust row of the Tier A matrix. The spec's claim for this row is that there
/// is no test file to classify, so <c>{files}</c> expansion degrades to the
/// full suite; these facts verify that and pin the two independent reasons it
/// happens.
/// </summary>
public sealed partial class TargetRepoMatrixTierATests
{
    /// <summary>
    /// A Cargo repo detects the whole-crate command and gains <c>cargo fmt</c>.
    /// Bootstrap pins <c>testFileCmd</c> to the same string, which carries no
    /// <c>{files}</c> token at all — so targeting is off from the first commit
    /// and the run-start footgun warning fires.
    /// </summary>
    [Fact]
    public async Task Row_Rust_DetectsCargoTestAndPinsATargetingFreeTestFileCmd()
    {
        var root = NewRepo("rust");
        try
        {
            Write(root, "Cargo.toml", "[package]\nname = \"sample\"\n");
            Write(root, "src/lib.rs", "#[cfg(test)]\nmod tests { #[test] fn ok() {} }\n");

            Assert.Equal(["cargo test"], TestCommandDetector.DetectCandidates(root));

            var (result, config) = await BootstrapAsync(root);

            Assert.Equal("cargo test", result.TestCommand);
            Assert.Contains("\"testFileCmd\": null", config, StringComparison.Ordinal);
            Assert.Contains("\"formatCmd\": \"cargo fmt\"", config, StringComparison.Ordinal);

            var loaded = RelayConfigLoader.Defaults("cargo test");
            Assert.NotNull(RelayDriver.TestFileCommandWarning(loaded));
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    /// <summary>
    /// Reason one for the degradation: idiomatic Rust unit tests live inside
    /// the implementation file behind <c>#[cfg(test)]</c>, so no path in the
    /// manifest classifies as a test file and expansion falls back to the full
    /// suite even when a <c>{files}</c> token IS configured.
    /// </summary>
    [Fact]
    public void Row_Rust_InlineCfgTestModule_LeavesNothingToExpand()
    {
        Assert.False(TestPathClassifier.IsRunnableTestFile("src/lib.rs", null));
        Assert.False(TestPathClassifier.IsTestRelated("src/lib.rs", null));

        var config = RelayConfigLoader.Defaults("cargo test") with
        {
            TestFileCommand = "cargo test {files}"
        };

        Assert.Equal("cargo test", RelayDriver.BuildTargetedTestCommand(config, ["src/lib.rs"]));
    }

    /// <summary>
    /// Reason two, and a defect rather than a degradation: a Rust integration
    /// test under <c>tests/</c> DOES classify, so the token expands — but cargo
    /// reads a bare positional argument as a test-NAME filter, not a path. The
    /// produced command matches no test and reports zero run, which is green.
    /// Asserted as-is so the wrong shape is visible, not endorsed.
    /// </summary>
    [Fact]
    public void Row_Rust_IntegrationTestExpandsToAPathCargoCannotUse()
    {
        Assert.True(TestPathClassifier.IsRunnableTestFile("tests/integration_test.rs", null));

        var config = RelayConfigLoader.Defaults("cargo test") with
        {
            TestFileCommand = "cargo test {files}"
        };

        Assert.Equal(
            "cargo test tests/integration_test.rs",
            RelayDriver.BuildTargetedTestCommand(config, ["src/lib.rs", "tests/integration_test.rs"]));
    }
}
