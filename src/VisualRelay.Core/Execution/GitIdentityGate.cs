namespace VisualRelay.Core.Execution;

/// <summary>
/// Refuses a run up front when git in the workspace has no identity to commit with.
/// <para>
/// The sealed commit is the pipeline's last stage, so without this check every paid
/// stage ran first and only then did git refuse. Measured in a fresh WSL distro whose
/// user has an empty full name: planning and stages 5 to 10 passed, then the commit
/// failed with "Author identity unknown ... empty ident name not allowed".
/// </para>
/// </summary>
public static class GitIdentityGate
{
    /// <summary>How much of git's own explanation the refusal carries.</summary>
    private const int OutputTailChars = 300;

    /// <summary>
    /// Asks git for the committer identity a commit in <paramref name="rootPath"/> would use.
    /// </summary>
    /// <param name="rootPath">The repository root.</param>
    /// <param name="git">Runs git where the workspace's git runs (inside the distro on Windows).</param>
    /// <param name="insideWsl">Whether that git runs inside a WSL distro, which is where the fix goes.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>
    /// <c>null</c> when git has an identity, or when the check itself timed out (an
    /// unanswered probe is not proof of a missing identity); otherwise the refusal.
    /// </returns>
    public static async Task<string?> CheckAsync(
        string rootPath, IGitInvoker git, bool insideWsl, CancellationToken cancellationToken = default)
    {
        var (exitCode, output, timedOut) = await git.RunAsync(rootPath, ["var", "GIT_COMMITTER_IDENT"], cancellationToken);
        if (exitCode == 0 || timedOut)
            return null;

        var where = insideWsl ? " inside the WSL distro" : string.Empty;
        return "Git has no identity to commit with in this project, so each task would run every stage and then "
            + $"fail at its commit. Set one{where}: git config --global user.name \"Your Name\" and "
            + $"git config --global user.email you@example.com. Git said: {Tail(output)}";
    }

    private static string Tail(string output)
    {
        var text = output.Trim().ReplaceLineEndings(" ");
        return text.Length <= OutputTailChars ? text : "…" + text[^OutputTailChars..];
    }
}
