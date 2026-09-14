using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

/// <summary>
/// A repository can keep a second toolchain for its own tooling. Measured on the Windows arm with
/// the-open-engine/zeroshot: 634 tracked Rust files, 14 JavaScript files, and a package.json whose
/// test script runs 4 tooling tests. Bootstrap validated that script first, so Verify would never
/// have run the 1193 tests of the Rust workspace. A candidate whose language has under a tenth of
/// the files of the best-represented candidate's language now ranks after it.
/// </summary>
public sealed partial class TestCommandDetectorTests
{
    private const string ToolingScript = "node --test tests/tooling/*.test.js";

    private static TestRepository RustWorkspaceWithAToolingScript()
    {
        var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "Cargo.toml"), "[workspace]\nresolver = \"2\"\n");
        File.WriteAllText(Path.Combine(repo.Root, "package.json"), $$"""{ "scripts": { "test": "{{ToolingScript}}" } }""");
        return repo;
    }

    [Fact]
    public void DetectToolchainCandidates_AToolingScriptBesideAFarLargerRustWorkspace_RanksCargoFirst()
    {
        using var repo = RustWorkspaceWithAToolingScript();

        var candidates = TestCommandDetector.DetectToolchainCandidates(
            repo.Root, new Dictionary<string, int> { [".rs"] = 634, [".json"] = 156, [".js"] = 14 });

        Assert.Equal(new TestCommandCandidate[] { new("cargo test", "rust"), new("npm test", "node") }, candidates);
    }

    [Fact]
    public void DetectToolchainCandidates_LanguagesOfComparableSize_KeepTheUsualOrder()
    {
        using var repo = RustWorkspaceWithAToolingScript();

        var candidates = TestCommandDetector.DetectToolchainCandidates(
            repo.Root, new Dictionary<string, int> { [".rs"] = 60, [".ts"] = 40 });

        Assert.Equal(["npm test", "cargo test"], candidates.Select(c => c.Command));
    }
}
