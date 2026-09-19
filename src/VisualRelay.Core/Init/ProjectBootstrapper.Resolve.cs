using VisualRelay.Core.Execution;

namespace VisualRelay.Core.Init;

/// <summary>
/// Choosing the test command bootstrap writes: the built-in candidates, then the
/// model's proposal, then the placeholder. Split from the main bootstrap file so each
/// stays within the 300-line guard.
/// </summary>
public static partial class ProjectBootstrapper
{
    /// <summary>The repository's tracked files, or none when it is not a repository yet.</summary>
    private static async Task<IReadOnlyList<string>> TrackedPathsAsync(
        string rootPath, IGitInvoker git, CancellationToken cancellationToken)
    {
        var (exitCode, output, timedOut) = await git.RunAsync(rootPath, ["ls-files", "-z"], cancellationToken);
        return exitCode != 0 || timedOut ? [] : GitPathOutput.SplitNulRecords(output);
    }

    // Detect candidates and return the first that smoke-validates; otherwise the
    // placeholder. The runner/timeout are injectable so callers (init vs. upgrade)
    // pick their own timeout and tests pass a fake.
    private static async Task<(string Command, bool UsedPlaceholder, SetupCheckDiagnostic? SetupCheck, IReadOnlyList<string> OtherCommands, TestCommandSource Source)> ResolveTestCommandAsync(
        string rootPath, TestLayoutDetection layout, ITestRunner? validationRunner, TimeSpan validationTimeout,
        SandboxHost host, ProposeTestCommand? proposeCommand, Func<string, ITestRunner>? proposalRunner,
        CancellationToken cancellationToken)
    {
        var detected = TestCommandDetector.DetectToolchainCandidates(rootPath, layout.CountsByExtension);
        var candidates = detected.Select(candidate => candidate.Command).ToList();
        var timeoutMs = (int)validationTimeout.TotalMilliseconds;
        var rejections = new List<(string, string, int, bool, string)>();
        if (candidates.Count > 0)
        {
            var runner = validationRunner ?? CreateValidationRunner(validationTimeout, host);
            var validator = new TestCommandValidator(runner);

            foreach (var candidate in detected)
            {
                var result = await validator.ValidateAsync(rootPath, candidate.Command, cancellationToken);
                if (result.Accepted)
                {
                    // Another toolchain's command is a suite Verify will not run, so the operator hears of it.
                    List<string> others =
                    [
                        .. detected.Where(other => other.Toolchain != candidate.Toolchain && !other.IsGuess)
                            .Select(other => other.Command),
                    ];
                    return (candidate.Command, false, null, others, TestCommandSource.Detected);
                }

                rejections.Add((
                    candidate.Command,
                    result.RejectionReason ?? "unknown",
                    result.RunResult.ExitCode,
                    result.RunResult.TimedOut,
                    result.RunResult.Output));
            }
        }

        // Nothing the table knows works here. Rather than answer the next unusual
        // repository with another marker rule, ask a small agent run that can read the
        // project's CI configuration, README and scripts — and keep its answer only if
        // it passes the same check a built-in candidate has to.
        if (await TryProposedCommandAsync(
                rootPath, layout, host, proposeCommand, proposalRunner, rejections, cancellationToken) is { } proposed)
        {
            return (proposed, false, null, [], TestCommandSource.Proposed);
        }

        if (rejections.Count > 0)
        {
            // Everything tried, nothing passed — summarize the highest-ranked one; the
            // artifact keeps them all, proposals included.
            var first = rejections[0];
            var diag = SetupCheckDiagnostic.FromFailedValidation(
                rootPath, first.Item1, timeoutMs, first.Item3, first.Item4, first.Item5, rejections);
            return (PlaceholderTestCommand, true, diag, [], TestCommandSource.Placeholder);
        }

        return (PlaceholderTestCommand, true, null, [], TestCommandSource.Placeholder);
    }

    /// <summary>
    /// Asks the proposer for a command and keeps the first one that passes the check,
    /// two rounds at most. Every proposal and its rejection joins
    /// <paramref name="rejections"/>, so <c>.relay/setup-check.log</c> shows everything
    /// that was tried rather than only what the table knew.
    /// <para>
    /// The proposal is checked under the SANDBOX the pipeline will run it through
    /// anyway, which is the right place for a command written by a model that has been
    /// reading an unfamiliar repository. A folder with no tracked source files is
    /// greenfield: there is nothing to find, so the proposer is never started.
    /// </para>
    /// </summary>
    private static async Task<string?> TryProposedCommandAsync(
        string rootPath,
        TestLayoutDetection layout,
        SandboxHost host,
        ProposeTestCommand? proposeCommand,
        Func<string, ITestRunner>? proposalRunner,
        List<(string, string, int, bool, string)> rejections,
        CancellationToken cancellationToken)
    {
        if (proposeCommand is null || layout.CountedFiles == 0)
            return null;

        var attempts = rejections
            .Select(r => new CommandAttempt(r.Item1, r.Item3, r.Item5))
            .ToList();
        var seen = new HashSet<string>(attempts.Select(a => a.Command), StringComparer.Ordinal);

        for (var round = 0; round < 2; round++)
        {
            var proposal = await proposeCommand(attempts, cancellationToken);
            // A repeated answer ends it: asking again produces the same command and
            // spends another agent run to learn nothing.
            if (string.IsNullOrWhiteSpace(proposal) || !seen.Add(proposal))
                return null;

            var runner = proposalRunner?.Invoke(proposal) ?? CreateProposalRunner(proposal, host);
            var result = await new TestCommandValidator(runner).ValidateAsync(rootPath, proposal, cancellationToken);
            if (result.Accepted)
                return proposal;

            var attempt = new CommandAttempt(proposal, result.RunResult.ExitCode, result.RunResult.Output);
            attempts.Add(attempt);
            rejections.Add((
                proposal,
                result.RejectionReason ?? "unknown",
                result.RunResult.ExitCode,
                result.RunResult.TimedOut,
                result.RunResult.Output));
        }

        return null;
    }

    /// <summary>
    /// The per-file command bootstrap is about to write, proven on real test files.
    /// The author-tests gate runs ONLY the test files a task wrote, through this
    /// command, and nothing ever ran it before the first task depended on it: where the
    /// table's form was wrong the gate then failed for the wrong reason, and a red that
    /// comes from a broken command is not proof that the new tests fail.
    /// <para>
    /// The table's form is tried first, then the proposer is asked with a second
    /// question. Nothing proven means null, and the gate runs the whole suite and says
    /// so, which is slower and still correct. With no test file in the repository there
    /// is nothing to prove with, so the table's form is written unproven.
    /// </para>
    /// </summary>
    private static async Task<(string? Command, PerFileCommandSource Source)> ResolvePerFileCommandAsync(
        string rootPath,
        string testCommand,
        IReadOnlyList<string> trackedPaths,
        ITestRunner? validationRunner,
        SandboxHost host,
        ProposePerFileCommand? proposePerFile,
        Func<string, ITestRunner>? proposalRunner,
        List<(string, string, int, bool, string)> rejections,
        CancellationToken cancellationToken)
    {
        var tableForm = TestCommandDetector.PerFileForm(testCommand, rootPath);
        var testFiles = PerFileCommandProof.ChooseTestFiles(rootPath, trackedPaths, null);
        if (testFiles.Count == 0)
            return (tableForm, PerFileCommandSource.Unproven);

        var runner = validationRunner ?? CreateValidationRunner(UpgradeValidationTimeout, host);
        if (tableForm is not null)
        {
            var proof = await PerFileCommandProof.ProveAsync(
                rootPath, tableForm, testFiles, runner, cancellationToken);
            if (proof.Proven)
                return (tableForm, PerFileCommandSource.Table);
            rejections.Add((tableForm, proof.Reason ?? "unknown", 0, false, proof.OutputHead));
        }

        if (proposePerFile is null)
            return (null, PerFileCommandSource.None);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var round = 0; round < 2; round++)
        {
            var proposal = await proposePerFile(testCommand, testFiles, tableForm, cancellationToken);
            if (string.IsNullOrWhiteSpace(proposal) || !seen.Add(proposal))
                break;

            var checkRunner = proposalRunner?.Invoke(proposal) ?? CreateProposalRunner(proposal, host);
            var proof = await PerFileCommandProof.ProveAsync(
                rootPath, proposal, testFiles, checkRunner, cancellationToken);
            if (proof.Proven)
                return (proposal, PerFileCommandSource.Proposed);
            rejections.Add((proposal, proof.Reason ?? "unknown", 0, false, proof.OutputHead));
        }

        return (null, PerFileCommandSource.None);
    }
}

