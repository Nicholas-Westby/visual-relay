using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayDriverManifestTests
{
    [Fact]
    public async Task RunTaskAsync_ManifestContainingTasksDirPath_DropsEntriesAndProceeds()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("bad-manifest", "# Bad manifest\n");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),
            new TestRunResult(0, "green"));
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, new BadManifestSubagentRunner(), tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "bad-manifest");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.DoesNotContain("manifest may not include task files", outcome.Reason ?? "", StringComparison.Ordinal);

        var manifestContent = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "bad-manifest", "manifest.txt"));
        Assert.Contains("src/real.cs", manifestContent, StringComparison.Ordinal);
        Assert.DoesNotContain("llm-tasks/", manifestContent, StringComparison.Ordinal);

        var ledgerContent = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "bad-manifest", "ledger.md"));
        Assert.Contains("> **Note**: dropped 1 task-dir entry from manifest: `llm-tasks/extra.md`", ledgerContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTaskAsync_ManifestWithOnlyTaskDirEntries_DropsAllAndProceeds()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("only-task-dir", "# Only task dir\n");
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, new OnlyTaskDirManifestSubagentRunner(), new ScriptedTestRunner(new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "only-task-dir");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.DoesNotContain("manifest may not include task files", outcome.Reason ?? "", StringComparison.Ordinal);

        var manifestContent = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "only-task-dir", "manifest.txt"));
        // Empty manifest: only the trailing newline written by WriteManifestAsync
        Assert.Equal(Environment.NewLine, manifestContent);

        var ledgerContent = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "only-task-dir", "ledger.md"));
        Assert.Contains("> **Note**: dropped 2 task-dir entries from manifest: `llm-tasks/a.md`, `llm-tasks/b.md`", ledgerContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTaskAsync_ArrayRootJson_FlagsCleanlyWithoutException()
    {
        // When a buggy ISubagentRunner returns IsValid=true with array-root JSON
        // (bypassing the extractor's object-root guard), the driver must detect
        // the invalid shape and flag cleanly — never throw an unhandled exception
        // that produces an "exception:" NEEDS-REVIEW.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("array-root", "# Array root\n");
        var runner = new ArrayRootSubagentRunner();
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, new ScriptedTestRunner(new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "array-root");

        Assert.Equal(RelayTaskOutcomeStatus.Flagged, outcome.Status);
        var review = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "array-root", "NEEDS-REVIEW"));
        Assert.DoesNotContain("exception:", review, StringComparison.Ordinal);
        // The error must describe the shape problem, not a raw InvalidOperationException.
        Assert.True(
            review.Contains("object", StringComparison.OrdinalIgnoreCase) ||
            review.Contains("shape", StringComparison.OrdinalIgnoreCase) ||
            review.Contains("array", StringComparison.OrdinalIgnoreCase),
            $"NEEDS-REVIEW must describe the shape problem; got: {review}");
    }

    [Fact]
    public async Task WriteManifestAsync_StripsPlusPrefixBeforePersisting()
    {
        // The '+' prefix signals "new file to be created" in the agent's JSON.
        // WriteManifestAsync must strip it before writing manifest.txt so the
        // persisted file contains clean paths only.
        var dir = Path.Combine(Path.GetTempPath(), "vr-manifest-strip", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var manifest = new[] { "src/existing.cs", "+src/NewFile.cs", "+tests/NewTest.cs" };
            await RelayDriver.WriteManifestAsync(dir, manifest, CancellationToken.None);

            var content = await File.ReadAllTextAsync(Path.Combine(dir, "manifest.txt"));
            Assert.Contains("src/existing.cs", content, StringComparison.Ordinal);
            Assert.Contains("src/NewFile.cs", content, StringComparison.Ordinal);
            Assert.Contains("tests/NewTest.cs", content, StringComparison.Ordinal);
            Assert.DoesNotContain("+", content, StringComparison.Ordinal);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(dir);
        }
    }
}
