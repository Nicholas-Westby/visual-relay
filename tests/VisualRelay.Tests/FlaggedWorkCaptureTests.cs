using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// A flag's capture is the only thing that keeps a flagged task's work once the drain
/// moves on. Measured on Windows 11 driving a repository inside WSL with no git
/// identity: commit-tree refused with "empty ident name not allowed", the capture
/// returned without a word, and the next task ran over the only copy of the work.
/// </summary>
public sealed class FlaggedWorkCaptureTests
{
    private const string TaskId = "flagged";

    /// <summary>What git said in the measured run, minus its advice paragraph.</summary>
    internal const string NoIdentity =
        "Author identity unknown\n\n*** Please tell me who you are.\n\n"
        + "fatal: empty ident name (for <alice@alice-pc.localdomain>) not allowed\n";

    /// <summary>
    /// The snapshot is Visual Relay's own plumbing commit and only ever lives inside the
    /// bundle, so it names its own author and committer instead of needing the user's.
    /// </summary>
    [Fact]
    public async Task Capture_NamesItsOwnAuthorAndCommitter_SoTheUsersIdentityIsNeverNeeded()
    {
        using var repo = TestRepository.Create();
        var recorder = new RecordingGitInvoker(SeedFlaggedWork(repo));

        var result = await CaptureAsync(repo, recorder);

        Assert.True(result.IsCaptured);
        Assert.True(File.Exists(Path.Combine(TaskDirectory(repo), FlaggedWorkStore.BundleFileName)));
        var environment = recorder.EnvironmentOf("commit-tree");
        Assert.NotNull(environment);
        foreach (var name in new[] { "GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL" })
            Assert.False(string.IsNullOrWhiteSpace(environment.GetValueOrDefault(name)), $"commit-tree must carry {name}");
    }

    [Fact]
    public async Task Capture_WhenCommitTreeIsRefused_ReportsTheStepAndWhatGitSaid()
    {
        using var repo = TestRepository.Create();
        var git = new FailingGitStepInvoker(SeedFlaggedWork(repo), ["commit-tree"], 128, NoIdentity);

        var result = await CaptureAsync(repo, git);

        Assert.True(result.IsFailed);
        Assert.False(result.IsCaptured);
        Assert.Equal("commit-tree", result.FailedStep);
        Assert.Contains("empty ident name (for <alice@alice-pc.localdomain>) not allowed", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(TaskDirectory(repo), FlaggedWorkStore.BundleFileName)));
    }

    [Fact]
    public async Task Capture_WhenBundleCreateFails_ReportsTheBundleStep()
    {
        using var repo = TestRepository.Create();
        var git = new FailingGitStepInvoker(SeedFlaggedWork(repo), ["bundle", "create"], 128,
            "fatal: cannot create '.relay/flagged/flagged-work.bundle': No space left on device\n");

        var result = await CaptureAsync(repo, git);

        Assert.Equal("bundle", result.FailedStep);
        Assert.Contains("No space left on device", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(TaskDirectory(repo), "flagged-work.json")));
    }

    [Fact]
    public async Task Capture_WhenAGitCallThrows_ReportsTheStepInsteadOfThrowing()
    {
        using var repo = TestRepository.Create();
        var git = new ThrowingGitStepInvoker(SeedFlaggedWork(repo), "write-tree");

        var result = await CaptureAsync(repo, git);

        Assert.Equal("write-tree", result.FailedStep);
        Assert.Contains("wsl.exe could not be started", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_KeepsOnlyTheTailOfWhatGitSaid()
    {
        using var repo = TestRepository.Create();
        var chatter = string.Concat(Enumerable.Repeat("warning: noise from a hook\n", 200));
        var git = new FailingGitStepInvoker(SeedFlaggedWork(repo), ["commit-tree"], 128, chatter + NoIdentity);

        var result = await CaptureAsync(repo, git);

        Assert.True(result.Output!.Length < 400, $"output kept {result.Output.Length} characters");
        Assert.EndsWith("not allowed", result.Output, StringComparison.Ordinal);
    }

    /// <summary>A run that recorded no run base has nothing to snapshot, and that is not a failure.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n")]
    public async Task Capture_WithNoRunBase_HasNothingToCapture(string? runBase)
    {
        using var repo = TestRepository.Create();
        var sim = SeedFlaggedWork(repo);
        var runBasePath = Path.Combine(TaskDirectory(repo), "run-base.txt");
        if (runBase is null)
            File.Delete(runBasePath);
        else
            File.WriteAllText(runBasePath, runBase);

        var result = await CaptureAsync(repo, sim);

        Assert.False(result.IsFailed);
        Assert.False(result.IsCaptured);
        Assert.Null(result.FailedStep);
    }

    private static string TaskDirectory(TestRepository repo) => Path.Combine(repo.Root, ".relay", TaskId);

    /// <summary>A repository with a recorded run base and one file of flagged work in its tree.</summary>
    private static GitSim.GitSim SeedFlaggedWork(TestRepository repo)
    {
        var sim = RelayDriverTestHelpers.InitTestRepo(repo);
        Directory.CreateDirectory(TaskDirectory(repo));
        File.WriteAllText(Path.Combine(TaskDirectory(repo), "run-base.txt"), sim.Head(repo.Root));
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "Feature.cs"), "// verified work");
        return sim;
    }

    private static Task<FlaggedWorkStore.CaptureResult> CaptureAsync(TestRepository repo, IGitInvoker git) =>
        FlaggedWorkStore.CaptureAsync(repo.Root, TaskId, TaskDirectory(repo), flaggedStage: 12,
            git, DateTimeOffset.UtcNow, CancellationToken.None);

    /// <summary>Throws from one git step, as a git that cannot even be launched does.</summary>
    private sealed class ThrowingGitStepInvoker(IGitInvoker inner, string step) : IGitInvoker
    {
        public Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
            string rootPath, IEnumerable<string> arguments, CancellationToken cancellationToken,
            TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken killToken = default, Action<string>? onActivity = null)
        {
            var args = arguments.ToArray();
            return args[0] == step
                ? throw new IOException("wsl.exe could not be started")
                : inner.RunAsync(rootPath, args, cancellationToken, timeout, environment, killToken, onActivity);
        }
    }
}
