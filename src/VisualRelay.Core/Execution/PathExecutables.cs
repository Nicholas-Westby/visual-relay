namespace VisualRelay.Core.Execution;

/// <summary>
/// The canonical "resolve an executable on PATH" helper, shared by the backend
/// venv probe, the git invoker, and the CLI gate probes so there is one
/// PATHEXT-aware implementation. On Windows a bare name (e.g. <c>uv</c>, <c>git</c>)
/// resolves only when one of its <c>%PATHEXT%</c> extensions (<c>.EXE</c>/<c>.CMD</c>/…)
/// is appended — there is no exec bit; on Unix the file must carry an execute bit.
/// </summary>
public static class PathExecutables
{
    /// <summary>The full path of <paramref name="name"/> on the current PATH, or null.</summary>
    public static string? Find(string name) =>
        Resolve(
            name,
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATHEXT"),
            OperatingSystem.IsWindows(),
            IsExecutableFile);

    /// <summary>True when <paramref name="name"/> resolves on the current PATH.</summary>
    public static bool OnPath(string name) => Find(name) is not null;

    /// <summary>
    /// Pure resolver: returns the full path of <paramref name="name"/> across the
    /// <paramref name="pathEnv"/> directories, or null. On Windows a bare name tries
    /// each <paramref name="pathext"/> extension; existence is decided by the
    /// injected <paramref name="exists"/> predicate so resolution is testable
    /// without touching the filesystem.
    /// </summary>
    public static string? Resolve(
        string name, string? pathEnv, string? pathext, bool isWindows, Func<string, bool> exists)
    {
        if (string.IsNullOrEmpty(pathEnv))
            return null;

        var extensions = isWindows
            ? (pathext ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        var nameHasExtension = Path.HasExtension(name);

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var bare = Path.Combine(dir, name);
            if (exists(bare))
                return bare;
            if (!isWindows || nameHasExtension)
                continue;
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, name + ext);
                if (exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// True when <paramref name="path"/> names a runnable file: a present file on
    /// Windows, an execute-bit file on Unix. The one shared predicate for "is this
    /// file executable" used by PATH resolution and the backend venv probe.
    /// </summary>
    public static bool IsExecutableFile(string path)
    {
        if (!File.Exists(path))
            return false;
        if (OperatingSystem.IsWindows())
            return true;
        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
