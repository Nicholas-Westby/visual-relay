using VisualRelay.Core.Execution;

namespace VisualRelay.GitSim;

/// <summary>
/// An in-memory <see cref="IGitInvoker"/> that emulates the exact git command
/// surface Visual Relay's production code drives, with zero process spawns and zero
/// real time — every call returns synchronously via <see cref="Task.FromResult"/>.
///
/// <para>State lives in a process-wide per-root registry (<see cref="State.GitSimRegistry"/>):
/// the object store, refs, HEAD and index. The working tree is the REAL filesystem
/// under <c>rootPath</c>. Arguments arrive WITHOUT the <c>-C</c> that the real
/// <c>GitInvoker</c> prepends, so <c>rootPath</c> is treated as the working directory.
/// <c>Output</c> is combined stdout+stderr in one string; <c>TimedOut</c> is always
/// false. An argv shape GitSim does not model throws
/// <see cref="InvalidOperationException"/> naming the full argv — never a silent
/// success. Seed and inspect repos through the API in <c>GitSim.Api.cs</c>.</para>
/// </summary>
public sealed partial class GitSim : IGitInvoker
{
    /// <summary>
    /// Consulted ONLY by <c>commit -m</c> (never <c>commit-tree</c>, as with real git).
    /// A rejecting verdict yields a non-zero exit with the message in <c>Output</c>.
    /// </summary>
    public Func<GitSimCommitRequest, GitSimHookVerdict>? PreCommitHook { get; set; }

    public Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
        string rootPath,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken killToken = default,
        Action<string>? onActivity = null)
    {
        var (args, quotePath) = StripGlobals(arguments);
        if (args.Count == 0)
            throw new InvalidOperationException("GitSim: empty git invocation");

        var context = new GitSimContext(rootPath, args, environment, PreCommitHook, quotePath);
        var result = GitSimCommandRouter.Dispatch(context);
        return Task.FromResult((result.ExitCode, result.Output, false));
    }

    /// <summary>
    /// Peels leading global options git accepts before the subcommand: repeated
    /// <c>-c key=value</c> (only <c>core.quotePath=false</c> is acted on — it forces
    /// literal path output). Anything else in that position is left for the router.
    /// </summary>
    private static (List<string> Args, bool QuotePath) StripGlobals(IEnumerable<string> arguments)
    {
        var all = arguments.ToList();
        var quotePath = true;
        var i = 0;
        while (i < all.Count)
        {
            if (all[i] == "-c" && i + 1 < all.Count)
            {
                if (all[i + 1] == "core.quotePath=false")
                    quotePath = false;
                i += 2;
                continue;
            }

            break;
        }

        return (all.Skip(i).ToList(), quotePath);
    }
}
