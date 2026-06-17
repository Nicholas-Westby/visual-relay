using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for the existence check added to
/// <see cref="SwivalSubagentRunner.CheckManifestAgainstGitignoreAsync"/>.
/// Each test calls the method directly with a JSON manifest and asserts
/// the corrective error (or null) for existence violations.
/// </summary>
public sealed class SwivalSubagentRunnerManifestExistenceTests : IDisposable
{
    private readonly string _root;

    public SwivalSubagentRunnerManifestExistenceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vr-manifest-existence-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        TestGit.Run(_root, "init");
        TestGit.Run(_root, "config", "user.email", "test@example.test");
        TestGit.Run(_root, "config", "user.name", "Test");
    }

    public void Dispose()
    {
        TestFileSystem.DeleteDirectoryResilient(_root);
    }

    [Fact]
    public async Task ManifestExistenceCheck_ExistingFilesPresent_ReturnsNull()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "status.cs"), "content");
        Directory.CreateDirectory(Path.Combine(_root, "tests"));
        File.WriteAllText(Path.Combine(_root, "tests", "status.test"), "content");

        var json = """{"plan":"edit files","manifest":["src/status.cs","tests/status.test"]}""";

        var error = await SwivalSubagentRunner.CheckManifestAgainstGitignoreAsync(
            json, stageNumber: 4, _root, CancellationToken.None);

        Assert.Null(error);
    }

    [Fact]
    public async Task ManifestExistenceCheck_MissingFile_ReturnsCorrectionMessage()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "status.cs"), "content");
        // src/ghost.cs does NOT exist on disk.

        var json = """{"plan":"edit files","manifest":["src/status.cs","src/ghost.cs"]}""";

        var error = await SwivalSubagentRunner.CheckManifestAgainstGitignoreAsync(
            json, stageNumber: 4, _root, CancellationToken.None);

        Assert.NotNull(error);
        Assert.Contains("does not exist", error, StringComparison.Ordinal);
        Assert.Contains("src/ghost.cs", error, StringComparison.Ordinal);
        // The existing file should not be flagged.
        Assert.DoesNotContain("src/status.cs", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManifestExistenceCheck_NewFilePrefixed_IsExempt()
    {
        // +src/NewFile.cs does NOT exist on disk, but the '+' prefix signals
        // "new file to be created" — it must be accepted without error.
        var json = """{"plan":"add feature","manifest":["+src/NewFile.cs"]}""";

        var error = await SwivalSubagentRunner.CheckManifestAgainstGitignoreAsync(
            json, stageNumber: 4, _root, CancellationToken.None);

        Assert.Null(error);
    }

    [Fact]
    public async Task ManifestExistenceCheck_MixedExistingAndMissing_ReportsOnlyMissing()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "status.cs"), "content");
        Directory.CreateDirectory(Path.Combine(_root, "tests"));
        File.WriteAllText(Path.Combine(_root, "tests", "status.test"), "content");
        // src/ghost.cs does NOT exist.

        var json = """{"plan":"edit files","manifest":["src/status.cs","tests/status.test","src/ghost.cs"]}""";

        var error = await SwivalSubagentRunner.CheckManifestAgainstGitignoreAsync(
            json, stageNumber: 4, _root, CancellationToken.None);

        Assert.NotNull(error);
        // Only the missing file named.
        Assert.Contains("src/ghost.cs", error, StringComparison.Ordinal);
        Assert.DoesNotContain("src/status.cs", error, StringComparison.Ordinal);
        Assert.DoesNotContain("tests/status.test", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManifestExistenceCheck_MissingAndGitignored_ReturnsExistenceErrorFirst()
    {
        // src/ghost.cs does NOT exist AND would be gitignored.
        // The existence error must surface first — it's the more actionable signal.
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "status.cs"), "content");
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "src/ghost.cs\n");
        // Re-init so git reads the new .gitignore.
        TestGit.Run(_root, "add", ".");
        TestGit.Run(_root, "commit", "-m", "seed");

        var json = """{"plan":"edit files","manifest":["src/status.cs","src/ghost.cs"]}""";

        var error = await SwivalSubagentRunner.CheckManifestAgainstGitignoreAsync(
            json, stageNumber: 4, _root, CancellationToken.None);

        Assert.NotNull(error);
        // Must be the existence error, not the gitignore error.
        Assert.Contains("does not exist", error, StringComparison.Ordinal);
        Assert.Contains("src/ghost.cs", error, StringComparison.Ordinal);
        Assert.DoesNotContain("gitignored", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManifestExistenceCheck_AmendManifest_Stage10_SameCheck()
    {
        // Stage 10 uses key "amendManifest". A missing path must trigger
        // the same corrective error as stage 4.
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "status.cs"), "content");
        // src/ghost.cs does NOT exist.

        var json = """{"summary":"fixed","amendManifest":["src/ghost.cs"]}""";

        var error = await SwivalSubagentRunner.CheckManifestAgainstGitignoreAsync(
            json, stageNumber: 10, _root, CancellationToken.None);

        Assert.NotNull(error);
        Assert.Contains("does not exist", error, StringComparison.Ordinal);
        Assert.Contains("src/ghost.cs", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManifestExistenceCheck_DirectoryEntry_IsAccepted()
    {
        // A manifest entry that names an existing directory (e.g. a project
        // root or a folder the agent intends to enumerate) must pass the
        // existence check — it is NOT a missing path.
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "src", "sub"));
        File.WriteAllText(Path.Combine(_root, "src", "status.cs"), "content");

        var json = """{"plan":"edit files","manifest":["src/status.cs","src/sub"]}""";

        var error = await SwivalSubagentRunner.CheckManifestAgainstGitignoreAsync(
            json, stageNumber: 4, _root, CancellationToken.None);

        Assert.Null(error);
    }

    [Fact]
    public async Task ManifestExistenceCheck_MissingNeitherFileNorDir_IsRejected()
    {
        // A path that is neither a file nor a directory must still produce
        // the "does not exist" rejection (regression guard for Bug 2 fix).
        var json = """{"plan":"edit files","manifest":["src/ghost.cs"]}""";

        var error = await SwivalSubagentRunner.CheckManifestAgainstGitignoreAsync(
            json, stageNumber: 4, _root, CancellationToken.None);

        Assert.NotNull(error);
        Assert.Contains("does not exist", error, StringComparison.Ordinal);
        Assert.Contains("src/ghost.cs", error, StringComparison.Ordinal);
    }
}
