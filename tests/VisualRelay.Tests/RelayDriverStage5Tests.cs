using System.Text.Json;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class RelayDriverTests
{
    // ═══════════════════════════════════════════════════════════════
    // stage-5 manifest merge: new test files added to manifest
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Stage5_ManifestMerge_AddsNewTestFilesToManifest()
    {
        // The agent authors a test file NOT in the stage-4 manifest.
        // After stage 5, the manifest must contain it.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("new-tests", "# Add new tests\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/status.cs", "tests/status.tests.cs");

        // Create the production file and commit so the repo is healthy.
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "src", "status.cs"), "old");
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "test@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Test");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "seed");

        // Override stage 5 to also author an extra test file not in the manifest.
        var wrapped = new ExtraTestFileRunner(runner, "tests/extra.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),
            new TestRunResult(0, "green"));
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(wrapped, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "new-tests");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // The manifest must contain the extra test file.
        var manifestContent = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, ".relay", "new-tests", "manifest.txt"));
        Assert.Contains("tests/extra.tests.cs", manifestContent, StringComparison.Ordinal);
        Assert.Contains("tests/status.tests.cs", manifestContent, StringComparison.Ordinal);
        Assert.Contains("src/status.cs", manifestContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stage5_ManifestMerge_DuplicateTestFilesNotReAdded()
    {
        // If a test file is already in the manifest, the merge must not
        // add it again (no duplicates).
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("dup-tests", "# Duplicate tests\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/status.cs", "tests/status.tests.cs");

        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "src", "status.cs"), "old");
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "test@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Test");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "seed");

        // Stage 5 returns testFiles that are already in the manifest.
        // ScriptedSubagentRunner already returns ["tests/status.tests.cs"] for stage 5,
        // and the manifest from stage 4 already contains that file. No duplicates.
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),
            new TestRunResult(0, "green"));
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "dup-tests");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        var manifestContent = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, ".relay", "dup-tests", "manifest.txt"));
        // "tests/status.tests.cs" should appear exactly once.
        var count = CountOccurrences(manifestContent, "tests/status.tests.cs");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Stage5_ManifestMerge_TaskDirTestFilesAreDropped()
    {
        // A testFile path under the tasks directory must be dropped and
        // noted in the ledger, matching the stage-4 guard behavior.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("taskdir-test", "# Task-dir test\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/status.cs", "tests/status.tests.cs");

        // Override stage 5 to return a test file under llm-tasks/.
        var wrapped = new ExtraTestFileRunner(runner, "llm-tasks/extra-test.md");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),
            new TestRunResult(0, "green"));
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(wrapped, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "taskdir-test");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // The ledger must note the drop.
        var ledgerContent = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, ".relay", "taskdir-test", "ledger.md"));
        Assert.Contains("dropped task-dir testFile", ledgerContent, StringComparison.Ordinal);
        Assert.Contains("llm-tasks/extra-test.md", ledgerContent, StringComparison.Ordinal);

        // The manifest must NOT contain the task-dir file.
        var manifestContent = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, ".relay", "taskdir-test", "manifest.txt"));
        Assert.DoesNotContain("llm-tasks/extra-test.md", manifestContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stage5_ManifestMerge_EmptyTestFiles_NoChange()
    {
        // When stage 5 returns no testFiles, the manifest must be unchanged.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("no-tests", "# No tests\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedNonCodeOnly("docs/README.md");

        Directory.CreateDirectory(Path.Combine(repo.Root, "docs"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "docs", "README.md"), "# Readme");
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "test@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Test");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "seed");

        var tests = new ScriptedTestRunner(new TestRunResult(0, "green"));
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "no-tests");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        var manifestContent = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, ".relay", "no-tests", "manifest.txt"));
        Assert.Contains("docs/README.md", manifestContent, StringComparison.Ordinal);
        // No test files should appear.
        Assert.DoesNotContain("tests/", manifestContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stage5_WorktreeFilter_DiscardsNonTestEdits()
    {
        // The agent authors test files AND edits a production file at stage 5.
        // WorktreeFilter discards the production edit (reverts to HEAD).
        // The test file survives, and stage 6 implements the production change.
        // The manifest includes the authored test file.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("prod-edit", "# Production edit\n");
        var runner = new PrematureImplementationRunner();

        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "src", "status.cs"), "old\n");
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "test@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Test");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "seed");

        var testRunner = new RedGateObservingTestRunner(repo.Root);
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, testRunner, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "prod-edit");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // Production file was implemented by stage 6.
        Assert.Equal("new\n", await File.ReadAllTextAsync(Path.Combine(repo.Root, "src", "status.cs")));

        // The test file survives both the filter and the commit.
        Assert.True(File.Exists(Path.Combine(repo.Root, "tests", "status.test")));

        // The manifest must include the test file.
        var manifestContent = await File.ReadAllTextAsync(
            Path.Combine(repo.Root, ".relay", "prod-edit", "manifest.txt"));
        Assert.Contains("tests/status.test", manifestContent, StringComparison.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════
    // Helper — wraps another runner and adds extra test files at stage 5
    // ═══════════════════════════════════════════════════════════════

    private sealed class ExtraTestFileRunner : ISubagentRunner
    {
        private readonly ISubagentRunner _inner;
        private readonly string _extraTestFile;

        public ExtraTestFileRunner(ISubagentRunner inner, string extraTestFile)
        {
            _inner = inner;
            _extraTestFile = extraTestFile;
        }

        public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
        {
            var result = await _inner.RunAsync(invocation, cancellationToken);

            // For stage 5, inject the extra test file into the JSON.
            if (invocation.Stage.Number == 5 && result.IsValid && !string.IsNullOrWhiteSpace(result.Json))
            {
                using var doc = JsonDocument.Parse(result.Json);
                var existing = new List<string>();
                if (doc.RootElement.TryGetProperty("testFiles", out var arr))
                {
                    existing.AddRange(arr.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0));
                }
                existing.Add(_extraTestFile);

                var rationale = doc.RootElement.TryGetProperty("rationale", out var r)
                    ? r.GetString() ?? "red first"
                    : "red first";

                var newJson = $$"""{"testFiles":{{JsonSerializer.Serialize(existing)}},"rationale":"{{rationale}}"}""";
                return new SubagentResult(newJson, newJson, true, null);
            }

            return result;
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0, pos = 0;
        while ((pos = text.IndexOf(value, pos, StringComparison.Ordinal)) >= 0)
        {
            count++;
            pos += value.Length;
        }
        return count;
    }
}
