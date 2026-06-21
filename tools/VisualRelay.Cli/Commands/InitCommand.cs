namespace VisualRelay.Cli.Commands;

/// <summary>
/// <c>init</c>: bootstraps a target folder. Prefers a published self-contained
/// <c>VisualRelay.Init</c> binary (brew installs ship it) and otherwise runs the
/// Init tool via <c>dotnet run</c>. With no explicit path, forwards the caller's
/// original working directory (ORIGINAL_CWD) so the correct repo is initialized.
/// </summary>
public static class InitCommand
{
    public static int Run(RepoPaths paths, IReadOnlyList<string> args)
    {
        var published = Path.Combine(paths.Root, "init", "VisualRelay.Init");
        if (IsExecutable(published))
            return ProcessLauncher.Run(published, args, paths.OriginalCwd);

        var proj = paths.ToolProject("VisualRelay.Init");
        var runArgs = new List<string> { "run", "--project", proj, "--" };
        if (args.Count == 0)
            runArgs.Add(paths.OriginalCwd);
        else
            runArgs.AddRange(args);
        return ProcessLauncher.Run(ProcessLauncher.Dotnet, runArgs, paths.Root);
    }

    private static bool IsExecutable(string path)
    {
        if (!File.Exists(path))
            return false;
        if (OperatingSystem.IsWindows())
            return true;
        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & UnixFileMode.UserExecute) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
