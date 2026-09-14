using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// The rewrite's guard profile self-heal, placed through the environment and the
/// host the caller hands in. Partial of <see cref="TaskRewriteRunnerTests"/>.
/// </summary>
public sealed partial class TaskRewriteRunnerTests
{
    // ── Sandbox profile self-heal (FIX 1) ──────────────────────────────────

    [Fact]
    public async Task RunAsync_EnsuresSandboxProfileExists_OnAFreshMachine()
    {
        // On a fresh machine no task has ever run, so the VR-owned nono profile
        // at $XDG_CONFIG_HOME/visual-relay/vr-guard.json does not exist yet. The
        // rewrite path invokes nono --profile <that path>; without an EnsureAsync
        // up front (mirroring RelayDriver.RunTaskAsync) nono fails. The rewrite
        // run must self-heal the profile before launching the sandboxed model.
        var (root, task, config, sim) = SetupRepo();
        var xdgRoot = Path.Combine(Path.GetTempPath(), "vr-rwr-xdg-" + Guid.NewGuid().ToString("N"));
        var env = new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = xdgRoot };
        var profilePath = NonoProfileEnsurer.ResolveProfilePath(env, SandboxHost.Local);
        try
        {
            Assert.False(File.Exists(profilePath),
                "pre-condition: a fresh machine has no vr-guard profile yet");

            var fake = new RewriteFakeRunner { NewContent = RewrittenSpec };

            var outcome = await TaskRewriteRunner.RunAsync(
                root, task, config, fake, sim, CancellationToken.None, environment: env, host: SandboxHost.Local);

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

    /// <summary>
    /// The self-heal uses the host it is handed, never this machine's: a Windows host
    /// with no resolved distro has nowhere to place the profile, so the rewrite refuses
    /// before a worktree exists or the model runs, and nothing lands locally instead.
    /// </summary>
    [Fact]
    public async Task RunAsync_OnAWindowsHostWithoutADistro_RefusesBeforeTheModelRuns()
    {
        var (root, task, config, sim) = SetupRepo();
        try
        {
            var originalBytes = await File.ReadAllBytesAsync(task.MarkdownPath, TestContext.Current.CancellationToken);
            var fake = new RewriteFakeRunner { NewContent = RewrittenSpec };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TaskRewriteRunner.RunAsync(
                root, task, config, fake, sim, CancellationToken.None,
                environment: TempXdg(root), host: SandboxHost.Windows(null)));

            Assert.Contains("no usable WSL2 distro", ex.Message, StringComparison.Ordinal);
            Assert.Null(fake.LastInvocation);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(task.MarkdownPath, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(NonoProfileEnsurer.ResolveProfilePath(TempXdg(root), SandboxHost.Local)),
                "a Windows host must never fall back to the local profile placement");
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }
}
