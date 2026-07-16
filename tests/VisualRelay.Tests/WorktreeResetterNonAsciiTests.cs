using VisualRelay.Core.Execution;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

public sealed class WorktreeResetterNonAsciiTests
{
    private static async Task WritePreRunSnapshot(string root, string taskId, params string[] entries)
    {
        var dir = Path.Combine(root, ".relay", taskId);
        Directory.CreateDirectory(dir);
        var content = string.Join(Environment.NewLine, entries);
        if (content.Length > 0)
            content += Environment.NewLine;
        await File.WriteAllTextAsync(Path.Combine(dir, "pre-run-untracked.txt"), content);
    }

    [Fact]
    public async Task ResetAsync_DeletesNonAsciiUntrackedFileAndReturnsRealName()
    {
        using var repo = TestRepository.Create();
        var sim = new GitSimEngine();
        sim.InitRepo(repo.Root);
        sim.Seed(repo.Root, "src/foo.cs", "content");
        sim.Commit(repo.Root, "seed");

        await WritePreRunSnapshot(repo.Root, "test-task"); // empty snapshot

        // Create an untracked file whose name contains U+202F.
        const string narrowNoBreakSpace = "\u202F";
        var realName = $"report{narrowNoBreakSpace}2026-07-01.log";
        var fullPath = Path.Combine(repo.Root, realName);
        await File.WriteAllTextAsync(fullPath, "log content");
        Assert.True(File.Exists(fullPath), "test setup: file must exist on disk");

        // git's default core.quotePath=true would emit this C-quoted form.
        var quotedName = "\"report\\342\\200\\2572026-07-01.log\"";
        var quotedInvoker = new QuotedLsFilesGitInvoker(sim, quotedName);

        var removed = await WorktreeResetter.ResetAsync(
            repo.Root, "test-task", tasksDir: null, CancellationToken.None, quotedInvoker);

        // The file must be actually deleted from disk.
        Assert.False(File.Exists(fullPath), "non-ASCII untracked file should be deleted");

        // The returned list must contain the REAL (decoded) path, not the C-quoted form.
        Assert.Contains(realName, removed);
        Assert.DoesNotContain(quotedName, removed);
    }

    private sealed class QuotedLsFilesGitInvoker(GitSimEngine inner, string quotedLsFilesOutput) : IGitInvoker
    {
        public async Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
            string rootPath,
            IEnumerable<string> arguments,
            CancellationToken cancellationToken,
            TimeSpan? timeout = null,
            IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken killToken = default,
            Action<string>? onActivity = null)
        {
            var args = arguments as IReadOnlyList<string> ?? arguments.ToList();

            if (args is ["-c", "core.quotePath=false", "ls-files", "--others", "--exclude-standard"])
            {
                var realResult = await inner.RunAsync(rootPath, args, cancellationToken, timeout, environment, killToken, onActivity);
                return (realResult.ExitCode, quotedLsFilesOutput, realResult.TimedOut);
            }

            return await inner.RunAsync(rootPath, args, cancellationToken, timeout, environment, killToken, onActivity);
        }
    }
}
