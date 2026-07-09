using System.Diagnostics;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// TDD-first tests for the graceful-stop window in <see cref="ProcessCapture"/>
/// and trace-presence fields in the killed-output autopsy header.
///
/// A watchdog-killed stage currently leaves zero trace evidence because
/// the kill is immediate SIGKILL — Python's atexit/finally handlers never
/// run, so the session trace .jsonl and report are never flushed.  The fix
/// introduces a SIGINT → 10 s grace → SIGKILL sequence (POSIX only) and
/// records trace-directory state in the killed-output header.
///
/// These tests assert the TARGET behaviour; they must FAIL before the
/// implementation exists.
/// </summary>
[Collection("Watchdog")]
public sealed class ProcessCaptureGracefulStopTests
{
    // ── ProcessCapture.RunAsync timeouts ───────────────────────────
    // Plenty of runway — the tests themselves take 1-12 s.
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);

    // The grace window the implementation will use (mirrors constant in
    // ProcessCapture.GracefulStop.cs once it exists).
    private static readonly TimeSpan GraceWindow = TimeSpan.FromSeconds(10);

    // ───────────────────────────────────────────────────────────────
    // Test 1 — SIGINT trapped → clean exit, evidence written
    // ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A child that traps SIGINT and writes a marker file must exit cleanly
    /// (code 0) when the kill token fires, with the marker file present on
    /// disk — proving the signal was delivered and handled.  Currently
    /// (immediate SIGKILL) the trap never runs and the marker is absent.
    /// </summary>
    [Fact]
    public async Task GracefulStop_ChildTrapsInt_ExitsCleanly()
    {
        SlowIntegration.SkipIfNotOptedIn();
        // Graceful stop is POSIX-only; on Windows the kill stays immediate.
        if (OperatingSystem.IsWindows())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), "vr-gst-1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var markerFile = Path.Combine(tempDir, "marker");
            // Escape the path for safe interpolation into the shell string.
            var escapedMarker = markerFile.Replace("'", "'\\''");
            // Use `sleep & wait` so bash is in the `wait` builtin (which is
            // interruptible by signals) rather than blocking on a foreground
            // external command (where bash defers trap handlers per POSIX).
            var script = $"trap 'touch \"{escapedMarker}\"; exit 0' INT; tail -f /dev/null & wait";

            using var killCts = new CancellationTokenSource();
            killCts.CancelAfter(500);

            var sw = Stopwatch.StartNew();
            var (exitCode, _, timedOut) = await ProcessCapture.RunAsync(
                "/bin/bash", $"-c \"{script}\"", tempDir, RunTimeout, CancellationToken.None,
                killToken: killCts.Token);
            sw.Stop();

            // The graceful-stop path ends with the child's own exit(0).
            Assert.False(timedOut, "Child should not hit the RunAsync timeout");
            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(markerFile),
                "Marker file must exist — child received SIGINT and ran its trap handler");
            Assert.True(sw.Elapsed < GraceWindow + TimeSpan.FromSeconds(3),
                $"Graceful exit should complete within grace window; took {sw.Elapsed.TotalSeconds:F1}s");
        }
        finally
        {
            // ReSharper disable once EmptyGeneralCatchClause — best-effort temp-dir cleanup
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // ───────────────────────────────────────────────────────────────
    // Test 2 — SIGINT ignored → hard kill after grace window
    // ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A child that explicitly ignores SIGINT must be force-killed AFTER
    /// the grace window expires.  Currently (immediate SIGKILL) the process
    /// dies in ~500 ms — the grace delay is absent.
    /// </summary>
    [Fact]
    public async Task GracefulStop_ChildIgnoresInt_ForceKilled()
    {
        SlowIntegration.SkipIfNotOptedIn();

        if (OperatingSystem.IsWindows())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), "vr-gst-2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // trap '' INT = set an empty handler → signal is ignored.
            // `exec tail` inherits SIG_IGN, so the child ignores SIGINT and must
            // be force-killed after the grace window.
            var script = "trap '' INT; exec tail -f /dev/null";

            using var killCts = new CancellationTokenSource();
            killCts.CancelAfter(500);

            var sw = Stopwatch.StartNew();
            var (exitCode, _, timedOut) = await ProcessCapture.RunAsync(
                "/bin/bash", $"-c \"{script}\"", tempDir, RunTimeout, CancellationToken.None,
                killToken: killCts.Token);
            sw.Stop();

            // The child ignores SIGINT → grace window expires → hard kill.
            Assert.True(exitCode != 0 || timedOut,
                "Child that ignores SIGINT must be force-killed (non-zero exit or timeout)");
            Assert.True(sw.Elapsed >= GraceWindow,
                $"Force kill must wait for grace window ({GraceWindow.TotalSeconds:F0}s); " +
                $"took {sw.Elapsed.TotalSeconds:F1}s");
            Assert.True(sw.Elapsed < GraceWindow + TimeSpan.FromSeconds(8),
                $"Force kill should fire shortly after grace window; " +
                $"took {sw.Elapsed.TotalSeconds:F1}s");
        }
        finally
        {
            // ReSharper disable once EmptyGeneralCatchClause — best-effort temp-dir cleanup
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // ───────────────────────────────────────────────────────────────
    // Test 3 — Windows retains immediate kill (no grace window)
    // ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Windows must keep the current immediate-kill behaviour.  The
    /// graceful-stop logic is guarded by <c>OperatingSystem.IsWindows()</c>
    /// and must not delay the kill on Windows.
    /// </summary>
    [Fact]
    public async Task GracefulStop_Windows_ImmediateKillUnchanged()
    {
        if (!OperatingSystem.IsWindows())
            return; // Graceful stop only applies to POSIX; test is Windows-only.

        var tempDir = Path.Combine(Path.GetTempPath(), "vr-gst-3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var script = "trap '' INT; exec tail -f /dev/null";

            using var killCts = new CancellationTokenSource();
            killCts.CancelAfter(500);

            var sw = Stopwatch.StartNew();
            var (exitCode, _, timedOut) = await ProcessCapture.RunAsync(
                "/bin/bash", $"-c \"{script}\"", tempDir, RunTimeout, CancellationToken.None,
                killToken: killCts.Token);
            sw.Stop();

            // On Windows: immediate kill, no grace delay.
            Assert.True(exitCode != 0 || timedOut, "Child must be killed");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
                $"Windows kill must be immediate (no grace window); " +
                $"took {sw.Elapsed.TotalSeconds:F1}s");
        }
        finally
        {
            // ReSharper disable once EmptyGeneralCatchClause — best-effort temp-dir cleanup
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // ───────────────────────────────────────────────────────────────
    // Test 4 — Autopsy header records trace presence (files exist)
    // ───────────────────────────────────────────────────────────────

    /// <summary>
    /// When a stage is watchdog-killed and trace files were written before
    /// the kill, the killed-output autopsy header must report the trace
    /// file count and byte total so future incidents are diagnosable.
    /// Currently the header only reports <c>bytes: N</c> — trace presence
    /// is invisible.
    /// </summary>
    [Fact]
    public async Task KilledOutput_HeaderIncludesTracePresence()
    {
        SlowIntegration.SkipIfNotOptedIn();

        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-with-trace",
            """
            #!/usr/bin/env bash
            while [[ $# -gt 0 ]]; do
              if [[ "$1" == "--trace-dir" ]]; then trace_dir="$2"; shift 2; else shift; fi
            done
            echo "pre-hang output" >&2
            mkdir -p "$trace_dir"
            printf '%s\n' '{"type":"assistant","message":{"content":[{"type":"text","text":"some work"}]}}' > "$trace_dir/trace.jsonl"
            exec tail -f /dev/null
            """);
        var config = TestConfig() with
        {
            InactivityTimeoutMsByTier = new Dictionary<string, int> { ["cheap"] = 3_000 },
            SubagentTimeoutMilliseconds = 8_000,  // backstop (inactivity 3s + ~5s)
            MaxStallRetries = 0
        };
        var runner = new SwivalSubagentRunner(config, script, backendProbe: SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        var result = await runner.RunAsync(SwivalTestHelpers.Invocation(repo.Root));

        Assert.False(result.IsValid);
        var persisted = Path.Combine(repo.Root, ".relay", "task", "stage1-attempt1.killed-output.txt");
        Assert.True(File.Exists(persisted), $"expected killed-output at {persisted}");
        var content = await File.ReadAllTextAsync(persisted);
        Assert.Contains("traceFiles:", content, StringComparison.Ordinal);
        Assert.Contains("traceBytes:", content, StringComparison.Ordinal);
        // Must report at least one trace file with non-zero bytes.
        Assert.DoesNotContain("traceFiles: 0", content, StringComparison.Ordinal);
    }

    // ───────────────────────────────────────────────────────────────
    // Test 5 — Autopsy header reports zero trace files (empty dir)
    // ───────────────────────────────────────────────────────────────

    /// <summary>
    /// When a stage is watchdog-killed and the trace directory is empty
    /// (the child never flushed), the autopsy header must still report
    /// the trace fields — <c>traceFiles: 0  traceBytes: 0</c> — making
    /// "lost" vs "flushed" legible without inspecting the directory.
    /// </summary>
    [Fact]
    public async Task KilledOutput_HeaderTraceFilesZero_WhenNoTraceFiles()
    {
        SlowIntegration.SkipIfNotOptedIn();

        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root,
            "fake-swival-no-trace",
            """
            #!/usr/bin/env bash
            echo "pre-hang output" >&2
            exec tail -f /dev/null
            """);
        var config = TestConfig() with
        {
            InactivityTimeoutMsByTier = new Dictionary<string, int> { ["cheap"] = 3_000 },
            SubagentTimeoutMilliseconds = 8_000,
            MaxStallRetries = 0
        };
        var runner = new SwivalSubagentRunner(config, script, backendProbe: SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        var result = await runner.RunAsync(SwivalTestHelpers.Invocation(repo.Root));

        Assert.False(result.IsValid);
        var persisted = Path.Combine(repo.Root, ".relay", "task", "stage1-attempt1.killed-output.txt");
        Assert.True(File.Exists(persisted), $"expected killed-output at {persisted}");
        var content = await File.ReadAllTextAsync(persisted);
        Assert.Contains("traceFiles: 0", content, StringComparison.Ordinal);
        Assert.Contains("traceBytes: 0", content, StringComparison.Ordinal);
    }

    // ── helpers ────────────────────────────────────────────────────

    private static RelayConfig TestConfig() =>
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
            5_000,
            300_000,
            new Dictionary<string, int>
            {
                ["cheap"] = 90_000,
                ["balanced"] = 120_000,
                ["frontier"] = 660_000
            },
            660_000,
            2,
            InactivityTimeoutMsByTier: null,
            InactivityTimeoutMs: 600_000);
}
