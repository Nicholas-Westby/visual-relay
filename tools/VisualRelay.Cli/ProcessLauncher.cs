using System.Diagnostics;

namespace VisualRelay.Cli;

/// <summary>
/// Thin foreground process runner: spawns a child with inherited stdio (so the
/// user sees live output and the child can prompt), waits, and returns the exit
/// code. This is the CLI's single shell-out point for <c>dotnet</c>, <c>nono</c>,
/// <c>swival</c>, and the guard scripts — it holds no command logic.
/// </summary>
public static class ProcessLauncher
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="arguments"/> in
    /// <paramref name="workingDirectory"/>, inheriting this process's stdio, and
    /// returns its exit code. <paramref name="extraEnv"/> entries are layered on
    /// top of the inherited environment. Returns 127 when the binary is missing.
    /// </summary>
    public static int Run(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            RedirectStandardInput = false,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);
        if (extraEnv is not null)
            foreach (var (k, v) in extraEnv)
                psi.Environment[k] = v;

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"failed to start {fileName}");
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"visual-relay: {fileName} not found on PATH");
            return 127;
        }
    }

    /// <summary>
    /// The dotnet binary the CLI shells out to. Honors the <c>VISUAL_RELAY_DOTNET</c>
    /// override (used by tests to intercept the CLI's own dotnet invocations,
    /// which .NET otherwise resolves against the running host rather than PATH);
    /// defaults to <c>dotnet</c> for production.
    /// </summary>
    public static string Dotnet
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("VISUAL_RELAY_DOTNET");
            return string.IsNullOrEmpty(overridden) ? "dotnet" : overridden;
        }
    }

    /// <summary>True when <paramref name="name"/> resolves on the current PATH.</summary>
    public static bool OnPath(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return false;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate) && IsExecutable(candidate))
                return true;
        }
        return false;
    }

    private static bool IsExecutable(string path)
    {
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
