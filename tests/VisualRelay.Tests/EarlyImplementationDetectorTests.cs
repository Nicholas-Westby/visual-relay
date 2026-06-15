using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class EarlyImplementationDetectorTests
{
    // IsImpl delegate matching RelayDriver's classifier: only files with a
    // recognised extension outside the non-code allowlist are implementation code.
    // This mirrors the single-sourced IsImpl on RelayDriver, which will be lifted
    // to internal static before the detector is written.
    private static readonly Func<string, bool> IsImpl = path =>
        Path.GetExtension(path) is { Length: > 0 } ext &&
        !new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".md", ".txt", ".json", ".yaml", ".yml", ".toml", ".csv" }.Contains(ext);

    // IsTestFile delegate mirroring RelayDriver's toolchain-agnostic heuristic:
    // paths under a tests/ directory, filenames matching *.tests.*, *_test.*,
    // or *.spec.* are treated as authored test files.
    private static readonly Func<string, bool> IsTestFile = path =>
    {
        var normalized = path.Replace('\\', '/');
        var fileName = Path.GetFileName(path);

        if (normalized.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/tests/", StringComparison.OrdinalIgnoreCase))
            return true;

        if (fileName.Contains(".tests.", StringComparison.OrdinalIgnoreCase))
            return true;

        if (Path.GetFileNameWithoutExtension(fileName)
                .EndsWith("_test", StringComparison.OrdinalIgnoreCase))
            return true;

        if (fileName.Contains(".spec.", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    };

    [Fact]
    public async Task ReturnsTrue_WhenTrackedImplFileModifiedVsHead()
    {
        using var repo = TestRepository.Create();
        InitGitRepo(repo.Root);

        // Modify a tracked impl file.
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "src", "x.cs"), "modified\n");

        var manifest = new[] { "src/x.cs" };
        var result = await EarlyImplementationDetector.ImplementationAlreadyUnderwayAsync(
            repo.Root, manifest, IsImpl, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task ReturnsTrue_WhenNewImplFileUntracked()
    {
        using var repo = TestRepository.Create();
        InitGitRepo(repo.Root);

        // Create a new, untracked impl file.
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "src", "new.cs"), "new file\n");

        // Manifest uses '+' prefix for new files.
        var manifest = new[] { "+src/new.cs" };
        var result = await EarlyImplementationDetector.ImplementationAlreadyUnderwayAsync(
            repo.Root, manifest, IsImpl, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task ReturnsFalse_WhenImplFilesCleanVsHead()
    {
        using var repo = TestRepository.Create();
        InitGitRepo(repo.Root);

        // Leave the committed file unchanged.
        var manifest = new[] { "src/x.cs" };
        var result = await EarlyImplementationDetector.ImplementationAlreadyUnderwayAsync(
            repo.Root, manifest, IsImpl, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task ReturnsFalse_WhenOnlyNonCodeFileModified()
    {
        using var repo = TestRepository.Create();
        InitGitRepo(repo.Root);

        // Modify a file with a non-code extension — IsImpl returns false for
        // .md files, so the detector must also return false.
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, "docs", "README.md"), "modified\n");

        var manifest = new[] { "docs/README.md" };
        var result = await EarlyImplementationDetector.ImplementationAlreadyUnderwayAsync(
            repo.Root, manifest, IsImpl, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task ReturnsFalse_WhenOnlyCodeExtensionTestFileModified()
    {
        using var repo = TestRepository.Create();
        InitGitRepo(repo.Root);

        // Modify a committed test file with a code extension (.cs) —
        // IsImpl returns true for .cs, but IsTestFile returns true for
        // paths under tests/, so the detector must exclude it and return false.
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, "tests", "x.tests.cs"), "modified\n");

        var manifest = new[] { "tests/x.tests.cs" };
        var result = await EarlyImplementationDetector.ImplementationAlreadyUnderwayAsync(
            repo.Root, manifest, IsImpl, CancellationToken.None, isTestFile: IsTestFile);

        Assert.False(result);
    }

    [Fact]
    public async Task ReturnsTrue_WhenNonTestImplFileModified()
    {
        using var repo = TestRepository.Create();
        InitGitRepo(repo.Root);

        // Modify a committed impl file that is NOT a test file —
        // IsImpl returns true and IsTestFile returns false, so the
        // detector must return true.
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, "src", "x.cs"), "modified\n");

        var manifest = new[] { "src/x.cs" };
        var result = await EarlyImplementationDetector.ImplementationAlreadyUnderwayAsync(
            repo.Root, manifest, IsImpl, CancellationToken.None, isTestFile: IsTestFile);

        Assert.True(result);
    }

    [Fact]
    public async Task ReturnsFalse_WhenManifestHasNoImplFiles()
    {
        using var repo = TestRepository.Create();
        InitGitRepo(repo.Root);

        // Manifest contains only a non-code file.
        var manifest = new[] { "docs/README.md" };
        var result = await EarlyImplementationDetector.ImplementationAlreadyUnderwayAsync(
            repo.Root, manifest, IsImpl, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task ReturnsFalse_OnNonGitRoot()
    {
        using var repo = TestRepository.Create();
        // Do NOT init git — plain temp dir.

        // Write an impl file that would be detected if this were a git repo.
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "src", "x.cs"), "content\n");

        var manifest = new[] { "src/x.cs" };
        var result = await EarlyImplementationDetector.ImplementationAlreadyUnderwayAsync(
            repo.Root, manifest, IsImpl, CancellationToken.None);

        // Must return false: no HEAD baseline available → safe-off.
        Assert.False(result);
    }

    [Fact]
    public async Task ReturnsFalse_OnEmptyManifest()
    {
        using var repo = TestRepository.Create();
        InitGitRepo(repo.Root);

        var result = await EarlyImplementationDetector.ImplementationAlreadyUnderwayAsync(
            repo.Root, [], IsImpl, CancellationToken.None);

        Assert.False(result);
    }

    private static void InitGitRepo(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "tests"));
        File.WriteAllText(Path.Combine(root, "src", "x.cs"), "original\n");
        File.WriteAllText(Path.Combine(root, "tests", "x.tests.cs"), "original\n");
        // Also create a non-code file so multi-file manifests have a non-impl entry.
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        File.WriteAllText(Path.Combine(root, "docs", "README.md"), "# README\n");
        TestGit.Run(root, "init");
        TestGit.Run(root, "config", "user.email", "visual-relay@example.test");
        TestGit.Run(root, "config", "user.name", "Visual Relay Tests");
        TestGit.Run(root, "add", ".");
        TestGit.Run(root, "commit", "-m", "chore: seed repo");
    }
}
