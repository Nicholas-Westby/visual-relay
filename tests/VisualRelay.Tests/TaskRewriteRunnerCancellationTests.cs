using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class TaskRewriteRunnerCancellationTests
{
    private const string OriginalSpec = "# Original\n\nDo the thing.\n";
    private const string RewrittenSpec = "# Rewritten\n\nBetter spec.\n";

    private static (string Root, RelayTaskItem Task, RelayConfig Config) SetupRepo(string taskId = "my-task")
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-rwc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        TestGit.Run(root, "init", "-q");
        TestGit.Run(root, "config", "user.email", "test@example.test");
        TestGit.Run(root, "config", "user.name", "Test");

        File.WriteAllText(Path.Combine(root, "README.md"), "# Repo\n");
        TestGit.Run(root, "add", ".");
        TestGit.Run(root, "commit", "-q", "-m", "seed");

        Directory.CreateDirectory(Path.Combine(root, ".relay"));
        File.WriteAllText(Path.Combine(root, ".relay", "config.json"),
            """{"testCmd":"dotnet test","logSources":[],"maxVerifyLoops":3}""");

        var taskDir = Path.Combine(root, "llm-tasks", taskId);
        Directory.CreateDirectory(taskDir);
        var markdownPath = Path.Combine(taskDir, $"{taskId}.md");
        File.WriteAllText(markdownPath, OriginalSpec);

        var task = new RelayTaskItem(
            Id: taskId,
            MarkdownPath: markdownPath,
            TaskDirectory: taskDir,
            IsNested: true,
            SiblingPaths: []);

        var config = RelayConfigLoader.Defaults();
        return (root, task, config);
    }

    [Fact]
    public async Task Cancellation_LeavesSpecByteIdentical()
    {
        var (root, task, config) = SetupRepo();
        try
        {
            var originalBytes = File.ReadAllBytes(task.MarkdownPath);

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var fake = new RewriteFakeRunner { NewContent = RewrittenSpec };

            var outcome = await TaskRewriteRunner.RunAsync(
                root, task, config, fake, cts.Token);

            Assert.False(outcome.Changed);
            Assert.NotNull(outcome.Error);

            var currentBytes = File.ReadAllBytes(task.MarkdownPath);
            Assert.Equal(originalBytes, currentBytes);

            // When cancelled before the runner is invoked, LastInvocation is null
            // and no worktree was created — just assert the spec is untouched.
            if (fake.LastInvocation is not null)
                Assert.False(Directory.Exists(fake.LastInvocation.TargetRoot),
                    "worktree must be removed after cancellation");
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    [Fact]
    public async Task Cancellation_AfterWorktreeCreation_StillCleansUp()
    {
        var (root, task, config) = SetupRepo();
        try
        {
            var originalBytes = File.ReadAllBytes(task.MarkdownPath);

            using var cts = new CancellationTokenSource();
            var fake = new PostWriteCancellationRunner(RewrittenSpec, cts.Token);

            var outcome = await TaskRewriteRunner.RunAsync(
                root, task, config, fake, CancellationToken.None);

            Assert.False(outcome.Changed);
            Assert.NotNull(outcome.Error);

            var currentBytes = File.ReadAllBytes(task.MarkdownPath);
            Assert.Equal(originalBytes, currentBytes);

            Assert.False(Directory.Exists(fake.WorktreeRoot),
                "worktree must be removed even after mid-run cancellation");
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }
}
