using VisualRelay.Core.Execution;

namespace VisualRelay.Core.Init;

/// <summary>
/// Creates a setup commit containing <c>.relay/config.json</c> and
/// <c>.relay/.gitignore</c> at the end of initialization, so those two files are
/// tracked from the start. Uses explicit pathspecs so user-staged changes are never
/// swept in. Idempotent: when both files are already tracked and unchanged, no
/// commit is made.
/// </summary>
public static class SetupCommitHelper
{
    private const string CommitMessage = "chore(relay): initialize project config";

    /// <summary>
    /// Stages and commits <c>.relay/config.json</c> and
    /// <c>.relay/.gitignore</c> with the canonical setup message. Returns null on
    /// success or when there is nothing to commit (idempotent). Returns a
    /// <see cref="SetupCheckDiagnostic"/> when the commit is rejected by the
    /// repo's own pre-commit hook — the files remain on disk and the failure is
    /// surfaced through the existing setup-check plumbing.
    /// </summary>
    public static async Task<SetupCheckDiagnostic?> TryCommitSetupFilesAsync(
        string rootPath,
        IGitInvoker? gitInvoker = null,
        CancellationToken cancellationToken = default)
    {
        var gi = gitInvoker ?? new GitInvoker();

        // Skip early when the folder isn't a git repository or has no HEAD yet
        // (GUI CreateConfigAsync on a non-git folder, or before the initial
        // commit). The caller may still be a valid VR init — we just can't commit.
        if (!await GitBootstrapper.IsRepositoryAsync(rootPath, gi, cancellationToken))
            return null;
        var headCheck = await gi.RunAsync(rootPath, ["rev-parse", "--verify", "--quiet", "HEAD"], cancellationToken);
        if (headCheck.ExitCode != 0)
            return null;

        var configPath = Path.Combine(rootPath, ".relay", "config.json");
        var gitignorePath = Path.Combine(rootPath, ".relay", ".gitignore");

        // Skip early when neither file exists on disk.
        if (!File.Exists(configPath) && !File.Exists(gitignorePath))
            return null;

        try
        {
            // Stage only our two files by explicit pathspec — never sweep in
            // independently staged user changes.
            var addResult = await gi.RunAsync(
                rootPath,
                ["add", "--", ".relay/config.json", ".relay/.gitignore"],
                cancellationToken);
            if (addResult.ExitCode != 0)
                return BuildHookRejectionDiagnostic(rootPath, addResult.Output);

            // Idempotence: if neither file differs from HEAD, bail without a commit.
            var diffResult = await gi.RunAsync(
                rootPath,
                ["diff", "--cached", "--quiet", "--", ".relay/config.json", ".relay/.gitignore"],
                cancellationToken);
            if (diffResult.ExitCode == 0)
                return null;

            // Commit with explicit pathspecs so ONLY our two files land.
            var commitResult = await gi.RunAsync(
                rootPath,
                ["commit", "-m", CommitMessage, "--", ".relay/config.json", ".relay/.gitignore"],
                cancellationToken);
            if (commitResult.ExitCode == 0)
                return null;

            // Hook (or other) rejection — surface through setup-check plumbing.
            return BuildHookRejectionDiagnostic(rootPath, commitResult.Output);
        }
        catch
        {
            // Unexpected infrastructure errors: leave files in place, don't crash
            // init, and don't misreport as a setup-check failure.
            return null;
        }
    }

    private static SetupCheckDiagnostic BuildHookRejectionDiagnostic(
        string rootPath, string output)
    {
        var command = $"git commit -m \"{CommitMessage}\" -- .relay/config.json .relay/.gitignore";
        var artifactPath = SetupCheckDiagnostic.WriteArtifact(
            rootPath, command, rootPath, timeoutMs: 30_000,
            exitCode: 1, timedOut: false, output);
        return new SetupCheckDiagnostic(
            Command: command,
            Cwd: rootPath,
            TimeoutMs: 30_000,
            ExitCode: 1,
            TimedOut: false,
            OutputTail: SetupCheckDiagnostic.CapForState(output),
            ArtifactPath: artifactPath,
            CapturedUtc: DateTimeOffset.UtcNow);
    }
}
