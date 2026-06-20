using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// A throwaway git repository under the git-ignored <c>.relay-scratch/</c> tree,
/// driven through a real <see cref="GitInvoker"/>. Used by the
/// <see cref="AuthorshipClaimerTests"/> integration tests; deleted on dispose.
/// </summary>
internal sealed class ScratchRepo : IDisposable
{
    public string Root { get; }

    private ScratchRepo(string root) => Root = root;

    public static ScratchRepo Create()
    {
        var baseDir = Path.Combine(RepoSetup.Root, ".relay-scratch", "claim-authorship-tests");
        var root = Path.Combine(baseDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new ScratchRepo(root);
    }

    public async Task InitAsync(IGitInvoker git, string authorName = "Managed via Tart", string authorEmail = "admin@tart.local")
    {
        await RunAsync(git, ["init", "-b", "main"]);
        await RunAsync(git, ["config", "user.name", authorName]);
        await RunAsync(git, ["config", "user.email", authorEmail]);
        await RunAsync(git, ["config", "commit.gpgsign", "false"]);
    }

    /// <summary>
    /// Writes a file and commits it with explicit author/committer identity and
    /// dates. Defaults to the foreign "Managed via Tart" identity, matching the
    /// real-world scenario the claimer fixes.
    /// </summary>
    public async Task SeedCommitAsync(
        IGitInvoker git, string fileName, string content, string message,
        string authorDate, string committerDate,
        string authorName = "Managed via Tart", string authorEmail = "admin@tart.local",
        string committerName = "Managed via Tart", string committerEmail = "admin@tart.local")
    {
        await File.WriteAllTextAsync(Path.Combine(Root, fileName), content);
        await RunAsync(git, ["add", fileName]);

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_AUTHOR_NAME"] = authorName,
            ["GIT_AUTHOR_EMAIL"] = authorEmail,
            ["GIT_AUTHOR_DATE"] = authorDate,
            ["GIT_COMMITTER_NAME"] = committerName,
            ["GIT_COMMITTER_EMAIL"] = committerEmail,
            ["GIT_COMMITTER_DATE"] = committerDate,
        };
        await RunAsync(git, ["commit", "--no-verify", "-m", message], env);
    }

    /// <summary>Creates a second branch, commits on it, and merges it into the
    /// current branch with a real merge commit (two parents).</summary>
    public async Task CreateMergeAsync(IGitInvoker git)
    {
        await RunAsync(git, ["checkout", "-b", "side"]);
        await File.WriteAllTextAsync(Path.Combine(Root, "side.txt"), "side");
        await RunAsync(git, ["add", "side.txt"]);
        await RunAsync(git, ["commit", "--no-verify", "-m", "feat: side"]);
        await RunAsync(git, ["checkout", "main"]);
        await File.WriteAllTextAsync(Path.Combine(Root, "main2.txt"), "main2");
        await RunAsync(git, ["add", "main2.txt"]);
        await RunAsync(git, ["commit", "--no-verify", "-m", "feat: main2"]);
        await RunAsync(git, ["merge", "--no-ff", "--no-edit", "side"]);
    }

    public async Task<string> HeadShaAsync(IGitInvoker git)
    {
        var (_, output, _) = await RunAsync(git, ["rev-parse", "HEAD"]);
        return output.Trim();
    }

    /// <summary>Returns author dates (ISO) oldest-&gt;newest for the last
    /// <paramref name="count"/> commits.</summary>
    public async Task<List<string>> AuthorDatesAsync(IGitInvoker git, int count)
    {
        var (_, output, _) = await RunAsync(git,
            ["log", "--reverse", $"-{count}", "--format=%aI"]);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// <summary>Returns per-commit identity + full message, oldest-&gt;newest.</summary>
    public async Task<List<CommitMeta>> CommitMetaAsync(IGitInvoker git, int count)
    {
        const string sep = "";
        const string recordSep = "";
        var (_, output, _) = await RunAsync(git,
            ["log", "--reverse", $"-{count}",
             $"--format=%an{sep}%ae{sep}%cn{sep}%ce{sep}%B{recordSep}"]);

        var rows = new List<CommitMeta>();
        foreach (var record in output.Split(recordSep, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = record.TrimStart('\n');
            if (trimmed.Length == 0)
                continue;
            var parts = trimmed.Split(sep);
            if (parts.Length < 5)
                continue;
            rows.Add(new CommitMeta(parts[0], parts[1], parts[2], parts[3], parts[4]));
        }

        return rows;
    }

    private async Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
        IGitInvoker git, IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var result = await git.RunAsync(Root, args, CancellationToken.None,
            timeout: TimeSpan.FromSeconds(30), environment: env);
        Assert.True(result.ExitCode == 0,
            $"git {string.Join(' ', args)} failed ({result.ExitCode}): {result.Output}");
        return result;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; scratch tree is git-ignored.
        }
    }
}

internal sealed record CommitMeta(
    string AuthorName, string AuthorEmail,
    string CommitterName, string CommitterEmail, string Body);
