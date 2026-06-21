namespace VisualRelay.Tests;

/// <summary>
/// Structural-convention checks for the split-oversized-test-files task.
/// Companion to SplitGuardVerificationTests.cs — every split must follow
/// the repo's sealed-partial-class + TestDoubles conventions.
/// </summary>
public sealed partial class SplitGuardVerificationTests
{
    /// <summary>Companion files must declare public sealed partial class matching the repo convention.</summary>
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
            Assert.Contains("public sealed partial class", content, StringComparison.Ordinal);
            // Companions inherit [Collection] — must not redundantly declare it.
            Assert.DoesNotContain("[Collection(", content, StringComparison.Ordinal);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // (GitCommitterCollectionFiles_HaveCollectionAttribute removed —
    //  the static GitCommitter.RawGitRunner seam has been replaced by
    //  an injected IGitInvoker; no collection serialization needed.)
    // ─────────────────────────────────────────────────────────────

    // (EnvironmentCollectionFiles_HaveCollectionAttribute removed —
    //  the static KeyEnvFile.EnvironmentAccessorOverride seam has been
    //  replaced by an injected IEnvironmentAccessor parameter.)
    // ─────────────────────────────────────────────────────────────

    // All six Avalonia headless classes must be in "Headless" — serializes them on the
    // single process-global dispatcher; non-headless collections run in parallel.
    [Fact]
    public void HeadlessCollectionFiles_HaveCollectionAttribute()
    {
        string[] expected =
        [
            "ActivityColumnItemsPanelTests.cs",
            "ActivityColumnTabsUiTests.cs",
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
            Assert.True(content.Contains("public sealed class", StringComparison.Ordinal)
                      || content.Contains("public sealed partial class", StringComparison.Ordinal),
                $"{fileName}: must declare public sealed class or public sealed partial class");
        }
    }

    /// <summary>Watchdog tests that launch real CPU-burning subprocesses must carry [Collection("Watchdog")].</summary>
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

    /// <summary>No test file may call Environment.SetEnvironmentVariable directly; all env mutation routes through the injected IEnvironmentAccessor.</summary>
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

    /// <summary>After the split the shared helpers must live in SwivalTestHelpers.cs, not duplicated across Swival* test files.</summary>
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

    /// <summary>After consolidation, private helpers must not appear as method definitions in individual Swival* files.</summary>
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

    // (GitInvokerTests_HasCollectionAttribute removed — the static
    //  GitInvoker.Override seam has been replaced by an injected
    //  IGitInvoker; no collection serialization is needed.)

    /// <summary>Every production git call site must route through GitInvoker; bare "git" strings in ProcessCapture/ProcessStartInfo are forbidden.</summary>
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

    /// <summary>No test file may reintroduce WaitUntilAsync/WaitUntilWithDispatcherAsync condition-polling helpers. Await the real operation Task instead (see harness-await-not-poll-async-tests).</summary>
    [Fact]
    public void NoTestFile_ReintroducesConditionPollHelper()
    {
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(TestsDir, "*.cs", SearchOption.TopDirectoryOnly))
        {
            // This convention file itself contains the banned strings as
            // documentation — skip it so it cannot self-tripwire.
            if (Path.GetFileName(file) == "SplitGuardVerificationTests.Conventions.cs") continue;

            var content = File.ReadAllText(file);
            if (content.Contains("WaitUntilAsync", StringComparison.Ordinal)
                || content.Contains("WaitUntilWithDispatcherAsync", StringComparison.Ordinal))
            {
                var relative = Path.GetRelativePath(RepoSetup.Root, file);
                violations.Add(
                    $"{relative}: contains WaitUntilAsync / WaitUntilWithDispatcherAsync " +
                    "polling helper — await the real operation Task instead. " +
                    "See harness-await-not-poll-async-tests.");
            }
        }

        Assert.Empty(violations);
    }

}
