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
                // Exclude guard companion files that contain "[Collection(" string
                // literals in assertions — they would false-positive this check.
                if (name.StartsWith("SplitGuardVerificationTests.", StringComparison.Ordinal)) return false;
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

    // Only KeyEnvFileTests.cs remains in "Environment" — the two UI classes moved to "Headless".
    [Fact]
    public void EnvironmentCollectionFiles_HaveCollectionAttribute()
    {
        foreach (var fileName in new[] { "KeyEnvFileTests.cs" })
        {
            var fullPath = Path.Combine(TestsDir, fileName);
            Assert.True(File.Exists(fullPath), $"Missing: {fileName}");
            var content = File.ReadAllText(fullPath);
            Assert.Contains("[Collection(\"Environment\")]", content, StringComparison.Ordinal);
            Assert.Contains("public sealed", content, StringComparison.Ordinal);
        }
    }

    // All six Avalonia headless classes must be in "Headless" — serializes them on the
    // single process-global dispatcher; non-headless collections run in parallel.
    [Fact]
    public void HeadlessCollectionFiles_HaveCollectionAttribute()
    {
        string[] expected =
        [
            "ActivityColumnItemsPanelTests.cs",
            "AddAttachmentsTests.cs",
            "ConfigInitEmptyStateUiTests.cs",
            "NewTaskAuthoringTests.cs",
            "KeySetupPanelUiTests.cs",
            "SettingsPanelUiTests.cs",
        ];

        foreach (var fileName in expected)
        {
            var fullPath = Path.Combine(TestsDir, fileName);
            Assert.True(File.Exists(fullPath), $"Missing: {fileName}");
            var content = File.ReadAllText(fullPath);
            Assert.Contains("[Collection(\"Headless\")]", content, StringComparison.Ordinal);
            Assert.Contains("public sealed class", content, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Watchdog tests that launch real CPU-burning or long-sleeping
    /// subprocesses must carry [Collection("Watchdog")] so they do not
    /// starve parallel timing-assertion tests.
    /// </summary>
    [Fact]
    public void WatchdogCollectionFiles_HaveCollectionAttribute()
    {
        string[] expected =
        [
            "SwivalSubagentRunnerWatchdogTests.cs",
        ];

        foreach (var fileName in expected)
        {
            var fullPath = Path.Combine(TestsDir, fileName);
            Assert.True(File.Exists(fullPath), $"Missing: {fileName}");
            var content = File.ReadAllText(fullPath);
            Assert.Contains("[Collection(\"Watchdog\")]", content, StringComparison.Ordinal);
            Assert.Contains("public sealed partial class", content, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// After env-mutation isolation, NO test file may call
    /// <c>Environment.SetEnvironmentVariable</c> directly — all env
    /// mutation routes through <c>KeyEnvFile.EnvironmentAccessorOverride</c>.
    /// </summary>
    [Fact]
    public void NoTestFile_CallsEnvironmentSetEnvironmentVariable()
    {
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(TestsDir, "*.cs", SearchOption.TopDirectoryOnly))
        {
            // The repoServices helper in TestDoubles.cs and TestGit.Setup serve
            // setup plumbing and do not race with parallel test classes.
            if (Path.GetFileName(file) == "TestDoubles.cs") continue;
            if (Path.GetFileName(file) == "RepoSetup.cs") continue;
            // SplitGuardVerificationTests.Conventions.cs itself checks
            // for this pattern as a string literal — skip it.
            if (Path.GetFileName(file) == "SplitGuardVerificationTests.Conventions.cs") continue;

            var content = File.ReadAllText(file);
            if (content.Contains("Environment.SetEnvironmentVariable", StringComparison.Ordinal))
            {
                var relative = Path.GetRelativePath(RepoSetup.Root, file);
                violations.Add($"{relative}: uses Environment.SetEnvironmentVariable");
            }
        }

        Assert.Empty(violations);
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

    // TransientGitShim must live in its own file (TransientGitShim.cs), not as
    // a private inner class in GitCommitterTests.cs.
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

    // ── GitInvoker architecture guard ──────────────────────────────────

    /// <summary>
    /// GitInvokerTests AND WorktreeFilterTests must carry
    /// [Collection("GitInvoker")] so every test that touches the static
    /// GitInvoker.Override seam (set it, or rely on it being null) is
    /// serialized and cannot race across collections.
    /// </summary>
    [Fact]
    public void GitInvokerTests_HasCollectionAttribute()
    {
        var path = Path.Combine(TestsDir, "GitInvokerTests.cs");
        Assert.True(File.Exists(path), "GitInvokerTests.cs must exist");
        var content = File.ReadAllText(path);
        Assert.Contains("[Collection(\"GitInvoker\")]", content, StringComparison.Ordinal);
        Assert.Contains("public sealed class GitInvokerTests", content, StringComparison.Ordinal);

        // WorktreeFilterTests must SHARE the collection (its real-git tests rely
        // on Override == null) — serialize it against GitInvokerTests. Attribute
        // on the MAIN partial only; companions inherit it.
        var wfPath = Path.Combine(TestsDir, "WorktreeFilterTests.cs");
        Assert.True(File.Exists(wfPath), "WorktreeFilterTests.cs must exist");
        var wfContent = File.ReadAllText(wfPath);
        Assert.Contains("[Collection(\"GitInvoker\")]", wfContent, StringComparison.Ordinal);
        Assert.Contains("public sealed partial class WorktreeFilterTests", wfContent, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every production git call site must route through
    /// <see cref="VisualRelay.Core.Execution.GitInvoker"/>. Bare <c>"git"</c> in
    /// <c>ProcessCapture.RunAsync</c> or <c>new ProcessStartInfo("git")</c> is
    /// forbidden — those fail under nix-shell environment rot on macOS.
    /// </summary>
    [Fact]
    public void NoBareGitString_InProductionCallSites()
    {
        var productionDirs = new[]
        {
            Path.Combine(RepoSetup.Root, "src", "VisualRelay.Core"),
            Path.Combine(RepoSetup.Root, "src", "VisualRelay.App"),
            Path.Combine(RepoSetup.Root, "src", "VisualRelay.Domain"),
        };

        var violations = new List<string>();

        foreach (var dir in productionDirs)
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                // Exclude generated and obj artifacts.
                if (file.Contains("/obj/") || file.Contains("\\obj\\")) continue;
                if (file.Contains("/bin/") || file.Contains("\\bin\\")) continue;

                var content = File.ReadAllText(file);
                var relative = Path.GetRelativePath(RepoSetup.Root, file);

                // Pattern 1: ProcessCapture.RunAsync("git", …)
                if (content.Contains(@"ProcessCapture.RunAsync(""git"",", StringComparison.Ordinal))
                {
                    violations.Add($"{relative}: ProcessCapture.RunAsync(\"git\", …) — use GitInvoker.RunAsync instead");
                }

                // Pattern 2: new ProcessStartInfo("git")
                if (content.Contains(@"new ProcessStartInfo(""git"")", StringComparison.Ordinal))
                {
                    violations.Add($"{relative}: new ProcessStartInfo(\"git\") — use GitInvoker.RunAsync instead");
                }
            }
        }

        Assert.Empty(violations);
    }

}
