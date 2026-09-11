namespace VisualRelay.Core.Execution.Wsl;

/// <summary>Where a git command for a workspace root runs.</summary>
public abstract record GitRoute
{
    private GitRoute() { }

    /// <summary>
    /// This OS's own git: Git for Windows for a drive root, the Unix git elsewhere.
    /// VR's own Windows checkout and its tooling stay on it.
    /// </summary>
    public sealed record Native : GitRoute;

    /// <summary>
    /// The git inside <paramref name="Distro"/>, against the Linux path, so VR and
    /// the sandboxed agent share one filesystem view of the repository.
    /// </summary>
    public sealed record Wsl(string Distro, string LinuxRoot) : GitRoute;
}

/// <summary>
/// Decides the route from the root path alone where it can: a WSL UNC root
/// names its own distro; a Linux absolute root belongs to the resolved distro
/// when there is one; a Windows drive root, a relative root and anything else
/// is native. Git for Windows over a <c>\\wsl$</c> share would be slow and would
/// see line endings and modes differently from the git the agent runs inside the
/// sandbox, and two views of one repository produce confusing diffs.
/// </summary>
public static class GitRouting
{
    public static GitRoute Decide(string rootPath, WslContext? wsl)
    {
        if (WslPath.TryParseUnc(rootPath, out var distro, out var linuxPath))
            return new GitRoute.Wsl(distro, linuxPath);
        if (wsl is not null && rootPath.StartsWith('/'))
            return new GitRoute.Wsl(wsl.Distro, rootPath);
        return new GitRoute.Native();
    }

    /// <summary>
    /// The wsl.exe launch for one routed git command: <c>-d &lt;distro&gt; --exec
    /// [env K=V...] git -C &lt;linuxRoot&gt; &lt;args...&gt;</c>. The caller's variables
    /// travel as <c>env</c> arguments because a wsl.exe child's Windows environment
    /// never crosses into Linux, and the sealed commit's RELAY_COMMIT_TOKEN must
    /// reach the pre-commit hook there; nothing else of VR's environment crosses,
    /// the distro has its own. Every element is one argv element, never quoted or joined.
    /// </summary>
    public static WslLaunch Launch(
        GitRoute.Wsl route, string wslExe, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment)
    {
        var argv = new List<string>();
        if (environment is { Count: > 0 })
        {
            argv.Add("env");
            foreach (var (name, value) in environment)
            {
                if (string.IsNullOrEmpty(name) || name.Contains('='))
                    throw new ArgumentException($"'{name}' is not a valid environment variable name.", nameof(environment));
                argv.Add($"{name}={value}");
            }
        }

        argv.Add("git");
        argv.Add("-C");
        argv.Add(route.LinuxRoot);
        argv.AddRange(arguments);
        return WslLauncher.BuildPlain(wslExe, route.Distro, argv);
    }
}
