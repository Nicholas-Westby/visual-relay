namespace VisualRelay.Tests;

/// <summary>
/// Structural-convention checks for the split-oversized-test-files task.
/// Companion to SplitGuardVerificationTests.cs — every split must follow
/// the repo's sealed-partial-class + TestDoubles conventions.
/// </summary>
public sealed partial class SplitGuardVerificationTests
{
    /// <summary>
    /// Every companion file (named <c>*Tests.*.cs</c>) must declare
    /// <c>public sealed partial class</c> matching the repo convention
    /// (GitCommitterAutoIncludeTests.Snapshot.cs, MainWindowViewModelTests.Status.cs).
    /// </summary>
    [Fact]
    public void CompanionFiles_DeclareSealedPartialClass()
    {
        var companionFiles = Directory.EnumerateFiles(TestsDir, "*.cs", SearchOption.TopDirectoryOnly)
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                // This file itself matches the companion pattern but its own
                // source contains "[Collection(" string literals used in
                // assertions — exclude it so the check doesn't fail on itself.
                if (name == "SplitGuardVerificationTests.Conventions.cs") return false;
                var idx = name.IndexOf("Tests.", StringComparison.Ordinal);
                if (idx < 0) return false;
                var after = name[(idx + "Tests.".Length)..];
                return after.EndsWith(".cs", StringComparison.Ordinal) && after.Length > ".cs".Length;
            })
            .ToList();

        Assert.NotEmpty(companionFiles);

        foreach (var file in companionFiles)
        {
            var content = File.ReadAllText(file);
            var relative = Path.GetRelativePath(RepoSetup.Root, file);
            Assert.Contains("public sealed partial class", content, StringComparison.Ordinal);
            // Companions inherit [Collection] — must not redundantly declare it.
            Assert.DoesNotContain("[Collection(", content, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Files in the GitCommitter collection must carry [Collection("GitCommitter")]
    /// on the main partial declaration. Companions inherit collection membership.
    /// </summary>
    [Fact]
    public void GitCommitterCollectionFiles_HaveCollectionAttribute()
    {
        string[] expected =
        [
            "GitCommitterAutoIncludeTests.cs",
            "GitCommitterTests.cs",
            "NoCommitContaminationTests.cs",
            "RelayDriverGitCommitTests.cs",
        ];

        foreach (var fileName in expected)
        {
            var fullPath = Path.Combine(TestsDir, fileName);
            Assert.True(File.Exists(fullPath), $"Missing: {fileName}");
            var content = File.ReadAllText(fullPath);
            Assert.Contains("[Collection(\"GitCommitter\")]", content, StringComparison.Ordinal);
            Assert.Contains("public sealed partial class", content, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// After the split the duplicated AlwaysReady, Invocation, and
    /// WriteExecutableAsync helpers must live in SwivalTestHelpers.cs
    /// so they are not repeated verbatim across six Swival* test files.
    /// </summary>
    [Fact]
    public void SwivalTestHelpers_ExistsAndContainsSharedMethods()
    {
        var path = Path.Combine(TestsDir, "SwivalTestHelpers.cs");
        Assert.True(File.Exists(path), "SwivalTestHelpers.cs must exist after the split");

        var content = File.ReadAllText(path);
        Assert.Contains("internal static", content, StringComparison.Ordinal);
        Assert.DoesNotContain("public sealed class", content, StringComparison.Ordinal);
        Assert.Contains("AlwaysReady", content, StringComparison.Ordinal);
        Assert.Contains("Invocation", content, StringComparison.Ordinal);
        Assert.Contains("WriteExecutableAsync", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// TransientGitShim must be moved from a private inner class in
    /// GitCommitterTests.cs to its own file (TransientGitShim.cs).
    /// </summary>
    [Fact]
    public void TransientGitShim_IsInOwnFile()
    {
        var shimPath = Path.Combine(TestsDir, "TransientGitShim.cs");
        Assert.True(File.Exists(shimPath));
        Assert.Contains("class TransientGitShim", File.ReadAllText(shimPath), StringComparison.Ordinal);

        var gitPath = Path.Combine(TestsDir, "GitCommitterTests.cs");
        Assert.DoesNotContain("class TransientGitShim", File.ReadAllText(gitPath), StringComparison.Ordinal);
    }

    /// <summary>
    /// After consolidation, the three duplicated private helpers must NOT
    /// appear as private method definitions in individual Swival* files.
    /// Check for the exact signature patterns that define these methods.
    /// </summary>
    [Fact]
    public void SwivalTestFiles_DoNotContainDuplicatedPrivateHelpers()
    {
        string[] swivalFiles =
        [
            "SwivalSubagentRunnerWatchdogTests.cs",
            "SwivalSubagentRunnerTests.cs",
            "SwivalSubagentRunnerCommandFilterTests.cs",
            "SwivalSubagentRunnerContractRetryTests.cs",
            "SwivalSubagentRunnerSandboxTests.cs",
            "SwivalSubagentRunnerGuardTests.cs",
        ];

        foreach (var fileName in swivalFiles)
        {
            var filePath = Path.Combine(TestsDir, fileName);
            Assert.True(File.Exists(filePath), $"Missing: {fileName}");
            var content = File.ReadAllText(filePath);

            // These signature fragments only appear when the method is DEFINED
            // (not called). After the split these definitions live in SwivalTestHelpers.
            Assert.DoesNotContain("static Task<BackendReadiness> AlwaysReady", content, StringComparison.Ordinal);
            Assert.DoesNotContain("static StageInvocation Invocation(string", content, StringComparison.Ordinal);
            Assert.DoesNotContain("static async Task<string> WriteExecutableAsync", content, StringComparison.Ordinal);
        }
    }
}
