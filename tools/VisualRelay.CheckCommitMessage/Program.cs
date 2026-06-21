using VisualRelay.CheckCommitMessage;
using VisualRelay.Core.CommitLint;
using VisualRelay.Core.Execution;

// VisualRelay.CheckCommitMessage — the C# commit-message validator that the
// .githooks/commit-msg wrapper execs. Modes:
//
//   <commit-msg-file>      Hook mode: validate one pending commit message.
//                          Driver tier (skip contextual rules) when
//                          RELAY_COMMIT_TOKEN matches the active-run nonce.
//   --check-history [range] Validate every commit in range (default: whole
//                          branch) under the FULL ruleset; no driver relaxation.
//   --rewrite-history <messages-dir> [range]
//                          Rewrite history: per commit, take a conforming message
//                          from <messages-dir>/<sha>.txt, else keep the original;
//                          replay through the in-process engine. This one WRITES.
//
// The two check modes are read-only (git reads + file reads only) — sandbox-safe
// mid-run. --rewrite-history rebuilds the branch and is NOT read-only.
//
// Exit codes: 0 = clean, 1 = violations / failure, 2 = usage error.

if (args.Length == 0)
    return Usage();

var git = new GitInvoker();
var cwd = Directory.GetCurrentDirectory();

if (args[0] == "--check-history")
{
    var range = args.Length > 1 ? args[1] : null;
    return await CheckHistoryAsync(git, cwd, range);
}

if (args[0] == "--rewrite-history")
{
    return await RewriteHistoryRunner.RunAsync(
        args[1..], cwd, git, Console.Out, Console.Error, CancellationToken.None);
}

return await CheckOneAsync(git, cwd, args[0]);

static int Usage()
{
    Console.Error.WriteLine(
        "usage: VisualRelay.CheckCommitMessage <commit-msg-file>\n"
        + "       VisualRelay.CheckCommitMessage --check-history [<range>]\n"
        + "       VisualRelay.CheckCommitMessage --rewrite-history <messages-dir> [<range>]");
    return 2;
}

async Task<int> CheckOneAsync(IGitInvoker gitInvoker, string startDir, string messageFile)
{
    if (!File.Exists(messageFile))
    {
        Console.Error.WriteLine($"VisualRelay.CheckCommitMessage: file not found: {messageFile}");
        return 2;
    }

    var repoRoot = await RepoRootAsync(gitInvoker, startDir);
    var message = await File.ReadAllTextAsync(messageFile);
    var token = Environment.GetEnvironmentVariable("RELAY_COMMIT_TOKEN");

    var tier = await CommitLintRunner.DecideTierAsync(repoRoot, token, gitInvoker, CancellationToken.None);
    var basenames = await CommitLintRunner.GatherChangedBasenamesAsync(repoRoot, gitInvoker, CancellationToken.None);
    var disallowed = CommitLintRunner.ReadDisallowedSubstrings(repoRoot);
    var context = new CommitLintContext(tier, basenames, disallowed);

    var violations = CommitMessageValidator.Validate(message, context);
    if (violations.Count == 0)
        return 0;

    Console.Error.Write(CommitLintRunner.FormatViolations(violations));
    return 1;
}

async Task<int> CheckHistoryAsync(IGitInvoker gitInvoker, string startDir, string? range)
{
    var repoRoot = await RepoRootAsync(gitInvoker, startDir);
    var rewriter = new HistoryRewriter(gitInvoker);
    var export = await rewriter.ExportAsync(repoRoot, range, CancellationToken.None);
    if (!export.Success || export.Commits is null)
    {
        Console.Error.WriteLine($"VisualRelay.CheckCommitMessage: {export.Error}");
        return 2;
    }

    var total = 0;
    foreach (var commit in export.Commits)
    {
        // History mode enforces the FULL ruleset — no driver relaxation.
        var context = CommitLintContext.Human(commit.ChangedBasenames, CommitLintRunner.ReadDisallowedSubstrings(repoRoot));
        var violations = CommitMessageValidator.Validate(commit.Message, context);
        if (violations.Count == 0)
            continue;

        total += violations.Count;
        var shortSha = commit.Sha[..Math.Min(8, commit.Sha.Length)];
        Console.Error.WriteLine($"commit {shortSha}: {violations.Count} violation(s)");
        foreach (var v in violations)
            Console.Error.WriteLine($"  - {v.Message}");
    }

    if (total == 0)
    {
        Console.WriteLine($"check-commit-message: {export.Commits.Count} commit(s) clean.");
        return 0;
    }

    Console.Error.WriteLine($"check-commit-message: {total} violation(s) across history. See {CommitRules.RulesDoc}.");
    return 1;
}

static async Task<string> RepoRootAsync(IGitInvoker gitInvoker, string startDir)
{
    var (exit, output, _) = await gitInvoker.RunAsync(
        startDir, ["rev-parse", "--show-toplevel"], CancellationToken.None, TimeSpan.FromSeconds(30));
    return exit == 0 && output.Trim().Length > 0 ? output.Trim() : startDir;
}
