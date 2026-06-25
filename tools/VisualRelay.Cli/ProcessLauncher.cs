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

        // Windows: a bare name (e.g. "git", "nono", "swival") resolves only when
        // its PATHEXT-implied extension (.EXE/.CMD/…) is appended — there is no
        // exec bit. The pure helper keeps that logic unit-testable on any OS.
        if (OperatingSystem.IsWindows())
            return ResolveOnPath(pathEnv, Environment.GetEnvironmentVariable("PATHEXT"), name, File.Exists);

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate) && IsExecutable(candidate))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Pure Windows PATH probe: resolves <paramref name="name"/> across every
    /// <paramref name="pathEnv"/> directory, trying the bare name and — when the
    /// name has no extension — each <paramref name="pathext"/> extension
    /// (e.g. <c>.COM;.EXE;.BAT;.CMD</c>). Existence is decided by the injected
    /// <paramref name="fileExists"/> predicate so the resolution is testable
    /// without touching the filesystem.
    /// </summary>
    public static bool ResolveOnPath(string? pathEnv, string? pathext, string name, Func<string, bool> fileExists)
    {
        if (string.IsNullOrEmpty(pathEnv))
            return false;

        var extensions = (pathext ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var nameHasExtension = Path.HasExtension(name);

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (fileExists(Path.Combine(dir, name)))
                return true;
            if (nameHasExtension)
                continue;
            foreach (var ext in extensions)
            {
                if (fileExists(Path.Combine(dir, name + ext)))
                    return true;
            }
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
