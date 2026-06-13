using System.Diagnostics;

namespace VisualRelay.Tests;

/// <summary>Tests for the <c>_timeout_watchdog</c> function and its dispatch
/// in <c>./visual-relay test</c> and <c>./visual-relay check</c>.</summary>
public sealed class VisualRelayTestCommandTimeoutTests
{
    private static string RepoRoot => RepoSetup.Root;
    private static string VisualRelayPath => Path.Combine(RepoRoot, "visual-relay");

    /// <summary>
    /// Guards: the test must be able to find ./visual-relay and bash must be
    /// available. Skip on Windows — the watchdog uses Unix process-group kill
    /// and job control that does not translate to cmd/pwsh.
    /// </summary>
    private static void EnsurePrerequisites()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Watchdog uses Unix job control (set -m, kill -PID); skip on Windows.");
        }

        if (!File.Exists(VisualRelayPath))
        {
            Assert.Skip($"visual-relay not found at {VisualRelayPath}");
        }
    }

    [Fact]
    public async Task Watchdog_FunctionIsDefined()
    {
        EnsurePrerequisites();

        var (exitCode, stdout, stderr) = await RunWatchdogTestAsync(
            "function-exists",
            """
            if ! declare -f _timeout_watchdog >/dev/null 2>&1; then
              echo "FAIL: _timeout_watchdog function not found in visual-relay" >&2
              exit 1
            fi
            echo "PASS"
            """);

        Assert.True(exitCode == 0, $"function-exists failed (exit {exitCode}): {stderr}");
        Assert.Contains("PASS", stdout);
    }

    [Fact]
    public async Task Watchdog_KillsHungCommand_AfterConfiguredTimeout()
    {
        EnsurePrerequisites();

        var (exitCode, stdout, stderr) = await RunWatchdogTestAsync(
            "kills-hung",
            """
            export VISUAL_RELAY_TEST_TIMEOUT=2

            start=$(date +%s)
            rc=0
            _timeout_watchdog sleep 999 || rc=$?
            end=$(date +%s)
            elapsed=$((end - start))

            # Must have been killed (non-zero exit)
            if [ "$rc" -eq 0 ]; then
              echo "FAIL: hung command should not exit 0 (got $rc)" >&2
              exit 1
            fi

            # Must have been killed within ~timeout + grace (1s SIGKILL grace)
            if [ "$elapsed" -gt 5 ]; then
              echo "FAIL: watchdog took too long (${elapsed}s, expected ~2s)" >&2
              exit 1
            fi

            echo "PASS rc=$rc elapsed=${elapsed}s"
            """);

        Assert.True(exitCode == 0, $"kills-hung failed (exit {exitCode}): {stderr}");
        Assert.Contains("PASS", stdout);
        Assert.Contains("timed out", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Watchdog_PrintsDiagnosticMessage_OnTimeout()
    {
        EnsurePrerequisites();

        var (exitCode, stdout, stderr) = await RunWatchdogTestAsync(
            "diagnostic-msg",
            """
            export VISUAL_RELAY_TEST_TIMEOUT=2

            rc=0
            _timeout_watchdog sleep 999 || rc=$?
            # Don't exit on non-zero — we only care about stderr content
            echo "PASS"
            """);

        Assert.True(exitCode == 0, $"diagnostic-msg failed (exit {exitCode}): {stderr}");

        // The watchdog must point at TROUBLESHOOTING.md and the --blame-hang diagnostic.
        var lowerStderr = stderr.ToLowerInvariant();
        Assert.Contains("troubleshooting.md", lowerStderr);
        Assert.Contains("--blame-hang", lowerStderr);
        Assert.Contains("--blame-hang-timeout", lowerStderr);
    }

    [Fact]
    public async Task Watchdog_PassesThroughFastCommand_Unaffected()
    {
        EnsurePrerequisites();

        var (exitCode, stdout, stderr) = await RunWatchdogTestAsync(
            "fast-pass-through",
            """
            export VISUAL_RELAY_TEST_TIMEOUT=60

            start=$(date +%s)
            rc=0
            _timeout_watchdog true || rc=$?
            end=$(date +%s)
            elapsed=$((end - start))

            # Must exit 0
            if [ "$rc" -ne 0 ]; then
              echo "FAIL: fast command should exit 0 (got $rc)" >&2
              exit 1
            fi

            # Must complete well under the timeout
            if [ "$elapsed" -gt 5 ]; then
              echo "FAIL: fast command took too long (${elapsed}s)" >&2
              exit 1
            fi

            echo "PASS elapsed=${elapsed}s"
            """);

        Assert.True(exitCode == 0, $"fast-pass-through failed (exit {exitCode}): {stderr}");
        Assert.Contains("PASS", stdout);

        // No timeout message on stderr for a fast command.
        Assert.DoesNotContain("timed out", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Watchdog_PassesThroughExitCode()
    {
        EnsurePrerequisites();

        var (exitCode, stdout, stderr) = await RunWatchdogTestAsync(
            "exit-code",
            """
            export VISUAL_RELAY_TEST_TIMEOUT=60

            rc=0
            _timeout_watchdog sh -c 'exit 42' || rc=$?

            if [ "$rc" -ne 42 ]; then
              echo "FAIL: expected exit code 42, got $rc" >&2
              exit 1
            fi

            echo "PASS"
            """);

        Assert.True(exitCode == 0, $"exit-code failed (exit {exitCode}): {stderr}");
        Assert.Contains("PASS", stdout);
        Assert.DoesNotContain("timed out", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Watchdog_RespectsTimeoutEnvVar()
    {
        EnsurePrerequisites();

        // With VISUAL_RELAY_TEST_TIMEOUT=3 the watchdog should wait ~3s (not the
        // default 60s) before killing.
        var (exitCode, stdout, stderr) = await RunWatchdogTestAsync(
            "respects-env",
            """
            export VISUAL_RELAY_TEST_TIMEOUT=3

            start=$(date +%s)
            rc=0
            _timeout_watchdog sleep 999 || rc=$?
            end=$(date +%s)
            elapsed=$((end - start))

            if [ "$rc" -eq 0 ]; then
              echo "FAIL: hung command should not exit 0" >&2
              exit 1
            fi

            # With a 3s timeout + 1s grace, elapsed should be 3–6s.
            if [ "$elapsed" -lt 2 ]; then
              echo "FAIL: watchdog fired too early (${elapsed}s for 3s timeout)" >&2
              exit 1
            fi
            if [ "$elapsed" -gt 7 ]; then
              echo "FAIL: watchdog took too long (${elapsed}s for 3s timeout)" >&2
              exit 1
            fi

            echo "PASS elapsed=${elapsed}s"
            """);

        Assert.True(exitCode == 0, $"respects-env failed (exit {exitCode}): {stderr}");
        Assert.Contains("PASS", stdout);
        Assert.Contains("timed out", stderr, StringComparison.OrdinalIgnoreCase);
    }

    // The check) case must wrap dotnet test in _timeout_watchdog so a
    // deadlocked suite self-terminates instead of stalling 34+ minutes.
    [Fact]
    public void Check_UsesTimeoutWatchdog()
    {
        EnsurePrerequisites();
        var script = File.ReadAllText(VisualRelayPath);
        var start = script.IndexOf("\n  check)\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "Could not find '  check)' case in visual-relay.");
        var after = script[(start + "\n  check)\n".Length)..];
        var end = after.IndexOf("\n    ;;\n", StringComparison.Ordinal);
        Assert.True(end >= 0, "Could not find end of check) case.");
        var body = after[..end];
        Assert.Contains("_timeout_watchdog", body);
        Assert.NotNull(
            Array.Find(body.Split('\n'),
                line => line.Contains("_timeout_watchdog") && line.Contains("dotnet test")));
    }

    // Extracts _timeout_watchdog from ./visual-relay into a temp script,
    // appends the test body, and runs it with bash.
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunWatchdogTestAsync(
        string name,
        string testBody)
    {
        var script = Path.Combine(Path.GetTempPath(), $"vr-watchdog-test-{name}.sh");
        var escapedVisualRelayPath = VisualRelayPath.Replace("'", "'\\''");

        var fullScript = $$"""
            #!/usr/bin/env bash
            set -euo pipefail

            eval "$(sed -n '/^_timeout_watchdog()/,/^}/p' '{{escapedVisualRelayPath}}')"

            {{testBody}}
            """;

        await File.WriteAllTextAsync(script, fullScript);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo("/bin/bash", script)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            // 15s safety cap: a correct watchdog kills its timer 'sleep' on
            // teardown; an orphaned 'sleep' would keep the pipe open and block.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await process.WaitForExitAsync(cts.Token);
                var stdout = await stdoutTask.WaitAsync(cts.Token);
                var stderr = await stderrTask.WaitAsync(cts.Token);
                return (process.ExitCode, stdout, stderr);
            }
            catch (OperationCanceledException)
            {
                // Kill the runaway process tree so we don't leak it.
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { /* already exited; orphan reaps itself at its own timeout */ }
                var partialStdout = stdoutTask.IsCompleted ? await stdoutTask : "";
                var partialStderr = stderrTask.IsCompleted ? await stderrTask : "";
                return (999, partialStdout,
                    partialStderr + "\n[C# safety timeout: process exit or output drain exceeded the deadline]");
            }
        }
        finally
        {
            try { File.Delete(script); } catch { /* best-effort */ }
        }
    }
}
