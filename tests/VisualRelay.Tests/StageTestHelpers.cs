using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Shared helpers for tests that drive a real stage command on disk.
/// <para>
/// These were consolidated out of six duplicated copies in the Swival runner's
/// test files. The runner is gone; the helpers are not about it — writing an
/// executable fixture and building an invocation are what any test that runs a
/// command still needs.
/// </para>
/// </summary>
internal static class StageTestHelpers
{
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

    /// <summary>
    /// Constructs a RelayConfig for SandboxedStage tests.
    /// Shared between SandboxedStageCommandFilterTests
    /// and SandboxedStageCommandFilterIntegrationTests.
    /// </summary>
    public static RelayConfig TestConfig(
        int frontendTimeoutMs = 5_000,
        int inactivityTimeoutMs = 300_000) =>
        new(
            "llm-tasks",
            "true",
            "true",
            [],
            new Dictionary<string, string> { ["cheap"] = "cheap" },
            true,
            1,
            1,
            false,
            true,
            frontendTimeoutMs,
            inactivityTimeoutMs,
            new Dictionary<string, int> { ["cheap"] = 90_000, ["balanced"] = 120_000, ["frontier"] = 660_000 },
            660_000,
            2);
}
