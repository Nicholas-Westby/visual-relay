using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Shared test helpers for Swival* test files. Consolidates the <c>AlwaysReady</c>,
/// <c>Invocation</c>, and <c>WriteExecutableAsync</c> helpers that were duplicated
/// verbatim across six test files.
/// </summary>
internal static class SwivalTestHelpers
{
    public static Task<BackendReadiness> AlwaysReady(CancellationToken _) =>
        Task.FromResult(new BackendReadiness(true, null));

    public static StageInvocation Invocation(string rootPath) =>
        new(
            RelayStages.All[0],
            "cheap",
            "run-1",
            rootPath,
            "task",
            "# Task",
            string.Empty,
            [],
            [],
            Path.Combine(rootPath, ".relay", "task", "stage1-attempt1"),
            Path.Combine(rootPath, ".relay", "task", "stage1-attempt1.report.json"),
            1);

    public static async Task<string> WriteExecutableAsync(string rootPath, string name, string text)
    {
        var path = Path.Combine(rootPath, name);
        await File.WriteAllTextAsync(path, text);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    /// <summary>
    /// Writes a transparent passthrough <c>nono</c> stub and returns its path. The
    /// sandbox is always on, so the runner always wraps swival as
    /// <c>nono run --profile … --allow-cwd [flags] -- &lt;swival&gt; &lt;args&gt;</c>.
    /// This stub skips everything up to and including the first <c>--</c>, then
    /// execs the remainder, so a unit test exercises the real nono-wrapped launch
    /// path while keeping the fake swival's stdout/stderr/timing fully under its
    /// control (no dependency on the real nono's Seatbelt/Landlock startup or its
    /// rollback preflight). Pass the returned path as the runner's
    /// <c>nonoBinary</c>.
    /// </summary>
    public static Task<string> WritePassthroughNonoAsync(string rootPath) =>
        WriteExecutableAsync(rootPath, "fake-nono-passthrough",
            """
            #!/usr/bin/env bash
            # Skip args until the first "--", then exec the wrapped command verbatim.
            while [[ $# -gt 0 ]]; do
              if [[ "$1" == "--" ]]; then shift; break; fi
              shift
            done
            exec "$@"
            """);
}
