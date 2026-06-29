using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class TaskRewriteRunnerTests
{
    private const string OriginalSpec = "# Original\n\nDo the thing.\n";
    private const string RewrittenSpec = "# Rewritten\n\nBetter spec.\n";

    private static void InitGitRepo(string root)
    {
        TestGit.Run(root, "init", "-q");
        TestGit.Run(root, "config", "user.email", "test@example.test");
        TestGit.Run(root, "config", "user.name", "Test");
    }

    private static void CommitAll(string root, string message)
    {
        TestGit.Run(root, "add", ".");
        TestGit.Run(root, "commit", "-q", "-m", message);
    }

    private static (string Root, RelayTaskItem Task, RelayConfig Config) SetupRepo(
        string taskId = "my-task",
        string? taskMarkdown = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-rwr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        InitGitRepo(root);

        File.WriteAllText(Path.Combine(root, "README.md"), "# Repo\n");
        CommitAll(root, "seed");

        Directory.CreateDirectory(Path.Combine(root, ".relay"));
        File.WriteAllText(Path.Combine(root, ".relay", "config.json"),
            """{"testCmd":"dotnet test","logSources":[],"enableFixVerify": true}""");

        var spec = taskMarkdown ?? OriginalSpec;
        var taskDir = Path.Combine(root, "llm-tasks", taskId);
        Directory.CreateDirectory(taskDir);
        var markdownPath = Path.Combine(taskDir, $"{taskId}.md");
        File.WriteAllText(markdownPath, spec);

        var task = new RelayTaskItem(
            Id: taskId,
            MarkdownPath: markdownPath,
            TaskDirectory: taskDir,
            IsNested: true,
            SiblingPaths: []);

        var config = RelayConfigLoader.Defaults();

        return (root, task, config);
    }

    // Pins XDG_CONFIG_HOME under the per-test repo so the once-per-run vr-guard
    // profile self-heal (TaskRewriteRunner.RunAsync → NonoProfileEnsurer.EnsureAsync)
    // writes there, never the real ~/.config — cleaned up with the repo root.
    private static DictionaryEnvironmentAccessor TempXdg(string root) =>
        new() { ["XDG_CONFIG_HOME"] = Path.Combine(root, ".xdg") };

    private static string ReadSpec(string root, string taskId)
        => File.ReadAllText(Path.Combine(root, "llm-tasks", taskId, $"{taskId}.md"));

    // ── Success path ──────────────────────────────────────────────────────

    [Fact]
    public async Task Success_CopiesOnlyTaskFolderBack()
    {
        var (root, task, config) = SetupRepo();
        try
        {
            var fake = new RewriteFakeRunner
            {
                NewContent = RewrittenSpec,
                WriteStrayFile = true,
                StrayRelativePath = "src/stray.txt"
            };

            var outcome = await TaskRewriteRunner.RunAsync(
                root, task, config, fake, CancellationToken.None, environment: TempXdg(root));

            Assert.Equal(RewrittenSpec, ReadSpec(root, task.Id));
            Assert.True(outcome.Changed);

            Assert.False(File.Exists(Path.Combine(root, "src", "stray.txt")),
                "stray writes outside the task folder must not be copied back");

            Assert.NotNull(fake.LastInvocation);
            Assert.False(Directory.Exists(fake.LastInvocation!.TargetRoot),
                "worktree must be removed after success");
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    [Fact]
    public async Task Success_PreservesPreExistingDirtyFile()
    {
        var (root, task, config) = SetupRepo();
        try
        {
            var dirtyPath = Path.Combine(root, "src", "dirty.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(dirtyPath)!);
            const string dirtyContent = "// uncommitted work\n";
            File.WriteAllText(dirtyPath, dirtyContent);

            var fake = new RewriteFakeRunner { NewContent = RewrittenSpec };

            var outcome = await TaskRewriteRunner.RunAsync(
                root, task, config, fake, CancellationToken.None, environment: TempXdg(root));

            Assert.True(outcome.Changed);
            Assert.Equal(RewrittenSpec, ReadSpec(root, task.Id));
            Assert.True(File.Exists(dirtyPath),
                "pre-existing dirty file must not be deleted");
            Assert.Equal(dirtyContent, File.ReadAllText(dirtyPath));
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    [Fact]
    public async Task Success_ReportsUnchanged_WhenSpecDidNotChange()
    {
        var (root, task, config) = SetupRepo();
        try
        {
            var fake = new RewriteFakeRunner { NewContent = OriginalSpec };

            var outcome = await TaskRewriteRunner.RunAsync(
                root, task, config, fake, CancellationToken.None, environment: TempXdg(root));

            Assert.False(outcome.Changed, "unchanged spec must report Changed=false");
            Assert.Equal(OriginalSpec, ReadSpec(root, task.Id));
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ── Error ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Error_LeavesSpecUntouched()
    {
        var (root, task, config) = SetupRepo();
        try
        {
            var originalBytes = File.ReadAllBytes(task.MarkdownPath);

            var fake = new RewriteFakeRunner
            {
                NewContent = RewrittenSpec,
                ThrowOnRun = true
            };

            var outcome = await TaskRewriteRunner.RunAsync(
                root, task, config, fake, CancellationToken.None, environment: TempXdg(root));

            Assert.False(outcome.Changed);
            Assert.NotNull(outcome.Error);
            Assert.Contains("synthetic", outcome.Error, StringComparison.OrdinalIgnoreCase);

            var currentBytes = File.ReadAllBytes(task.MarkdownPath);
            Assert.Equal(originalBytes, currentBytes);

            Assert.NotNull(fake.LastInvocation);
            Assert.False(Directory.Exists(fake.LastInvocation!.TargetRoot),
                "worktree must be removed after error");
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    [Fact]
    public async Task Error_PreservesDiagnosticFile_AfterWorktreeRemoved()
    {
        // The rewrite worktree is deleted on failure (finally → RemoveAsync), which
        // also deletes the diagnostic the (full output: …) breadcrumb points at. A
        // failed rewrite must preserve that diagnostic OUT of the worktree so the
        // breadcrumb resolves to a file that still exists.
        var (root, task, config) = SetupRepo();
        try
        {
            var fake = new RewriteDiagnosticFailureRunner();

            var outcome = await TaskRewriteRunner.RunAsync(
                root, task, config, fake, CancellationToken.None, environment: TempXdg(root));

            Assert.False(outcome.Changed);
            Assert.NotNull(outcome.Error);
            Assert.NotEmpty(fake.DiagnosticRelativePath);

            // The breadcrumb in the surfaced error must point at a file that still
            // exists (preserved under the main tree, not the deleted worktree).
            var match = System.Text.RegularExpressions.Regex.Match(
                outcome.Error!, @"\(full output: (?<path>.+?)\)");
            Assert.True(match.Success, $"error must keep a (full output: …) breadcrumb: {outcome.Error}");
            var breadcrumb = match.Groups["path"].Value;
            Assert.True(File.Exists(breadcrumb),
                $"preserved diagnostic must exist after worktree removal: {breadcrumb}");
            Assert.Contains(RewriteDiagnosticFailureRunner.DiagnosticContents,
                await File.ReadAllTextAsync(breadcrumb), StringComparison.Ordinal);

            // The breadcrumb must NOT point into a (now-deleted) rewrite worktree.
            Assert.DoesNotContain("rewrite-", breadcrumb, StringComparison.Ordinal);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ── Sandbox profile self-heal (FIX 1) ──────────────────────────────────

    [Fact]
    public async Task RunAsync_EnsuresSandboxProfileExists_OnAFreshMachine()
    {
        // On a fresh machine no task has ever run, so the VR-owned nono profile
        // at $XDG_CONFIG_HOME/visual-relay/vr-guard.json does not exist yet. The
        // rewrite path invokes nono --profile <that path>; without an EnsureAsync
        // up front (mirroring RelayDriver.RunTaskAsync) nono fails. The rewrite
        // run must self-heal the profile before launching the sandboxed model.
        var (root, task, config) = SetupRepo();
        var xdgRoot = Path.Combine(Path.GetTempPath(), "vr-rwr-xdg-" + Guid.NewGuid().ToString("N"));
        var env = new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = xdgRoot };
        var profilePath = NonoProfileEnsurer.ResolveProfilePath(env);
        try
        {
            Assert.False(File.Exists(profilePath),
                "pre-condition: a fresh machine has no vr-guard profile yet");

            var fake = new RewriteFakeRunner { NewContent = RewrittenSpec };

            var outcome = await TaskRewriteRunner.RunAsync(
                root, task, config, fake, CancellationToken.None, environment: env);

            Assert.True(outcome.Changed);
            Assert.True(File.Exists(profilePath),
                "the rewrite run must ensure the sandbox profile exists before launching nono");
            Assert.Equal(NonoProfileEnsurer.EmbeddedContent, await File.ReadAllTextAsync(profilePath));
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(xdgRoot);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ── Isolation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Success_DoesNotCopySiblingTaskFolders()
    {
        var (root, task, config) = SetupRepo("task-a");
        try
        {
            var siblingDir = Path.Combine(root, "llm-tasks", "task-b");
            Directory.CreateDirectory(siblingDir);
            File.WriteAllText(Path.Combine(siblingDir, "task-b.md"), "# Task B\n");

            var fake = new RewriteFakeRunner
            {
                NewContent = RewrittenSpec,
                WriteStrayFile = true,
                StrayRelativePath = "llm-tasks/task-b/task-b.md",
                StrayContent = "# Tampered B\n"
            };

            var outcome = await TaskRewriteRunner.RunAsync(
                root, task, config, fake, CancellationToken.None, environment: TempXdg(root));

            Assert.True(outcome.Changed);
            Assert.Equal(RewrittenSpec, ReadSpec(root, "task-a"));
            Assert.Equal("# Task B\n",
                File.ReadAllText(Path.Combine(root, "llm-tasks", "task-b", "task-b.md")));
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }
}
