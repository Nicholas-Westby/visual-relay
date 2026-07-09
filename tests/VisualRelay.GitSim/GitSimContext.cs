using VisualRelay.GitSim.State;

namespace VisualRelay.GitSim;

/// <summary>Exit code + combined stdout/stderr for one simulated git invocation.</summary>
internal readonly record struct GitSimResult(int ExitCode, string Output)
{
    public static GitSimResult Ok(string output = "") => new(0, output);
    public static GitSimResult Code(int exitCode, string output = "") => new(exitCode, output);

    /// <summary>A <c>fatal:</c>-prefixed failure at git's usual exit 128.</summary>
    public static GitSimResult Fatal(string message) => new(128, $"fatal: {message}\n");
}

/// <summary>
/// Everything one command handler needs: the raw root path, the argv with global
/// options already stripped, the invocation environment, and the caller's optional
/// pre-commit hook. Also the argv-navigation and repo-lookup helpers the handlers
/// share. A missing repo is a normal <c>fatal: not a git repository</c> outcome — an
/// UNSUPPORTED argv shape instead throws (see <see cref="Unsupported"/>).
/// </summary>
internal sealed class GitSimContext(
    string root,
    IReadOnlyList<string> args,
    IReadOnlyDictionary<string, string>? environment,
    Func<GitSimCommitRequest, GitSimHookVerdict>? preCommitHook,
    bool quotePath)
{
    public string Root { get; } = root;
    public IReadOnlyList<string> Args { get; } = args;
    public IReadOnlyDictionary<string, string> Environment { get; } =
        environment ?? new Dictionary<string, string>(StringComparer.Ordinal);
    public Func<GitSimCommitRequest, GitSimHookVerdict>? PreCommitHook { get; } = preCommitHook;
    public bool QuotePath { get; } = quotePath;

    /// <summary>The command word (argv[0]).</summary>
    public string Command => Args[0];

    /// <summary>argv after the command word.</summary>
    public IReadOnlyList<string> Rest => Args.Skip(1).ToList();

    public bool Has(string token) => Args.Contains(token, StringComparer.Ordinal);

    /// <summary>The token immediately after <paramref name="flag"/>, or null.</summary>
    public string? ValueAfter(string flag)
    {
        for (var i = 0; i < Args.Count - 1; i++)
            if (Args[i] == flag)
                return Args[i + 1];
        return null;
    }

    /// <summary>Everything after a literal <c>--</c> separator (the pathspec list), or empty.</summary>
    public IReadOnlyList<string> Pathspecs()
    {
        var idx = Args.ToList().IndexOf("--");
        return idx < 0 ? [] : Args.Skip(idx + 1).ToList();
    }

    public Worktree? Repo => GitSimRegistry.Find(Root);

    public bool TryRepo(out Worktree worktree)
    {
        var found = GitSimRegistry.Find(Root);
        worktree = found!;
        return found is not null;
    }

    public GitIndex Index(Worktree worktree) => worktree.IndexFor(Environment);

    /// <summary>Signals an argv shape GitSim does not emulate — never silently succeed.</summary>
    public GitSimResult Unsupported() =>
        throw new InvalidOperationException(
            $"GitSim: unsupported git invocation: git {string.Join(' ', Args)}");
}
