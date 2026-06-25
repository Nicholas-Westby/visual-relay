using System.Diagnostics;
using VisualRelay.Core.Execution;

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

    /// <summary>True when <paramref name="name"/> resolves on the current PATH.
    /// Delegates to the shared PATHEXT-aware resolver so the CLI gates, the backend
    /// venv probe, and the git invoker all agree on one resolution.</summary>
    public static bool OnPath(string name) => PathExecutables.OnPath(name);

    /// <summary>
    /// Pure Windows PATH probe kept for the gate tests: true when
    /// <paramref name="name"/> resolves across <paramref name="pathEnv"/>, trying
    /// each <paramref name="pathext"/> extension for a bare name. Existence is the
    /// injected <paramref name="fileExists"/> predicate. Thin wrapper over the
    /// shared resolver's Windows branch.
    /// </summary>
    public static bool ResolveOnPath(string? pathEnv, string? pathext, string name, Func<string, bool> fileExists) =>
        PathExecutables.Resolve(name, pathEnv, pathext, isWindows: true, fileExists) is not null;
}
