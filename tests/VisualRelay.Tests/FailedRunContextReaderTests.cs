using System.Text.Json;
using VisualRelay.Core.Tasks;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for <see cref="FailedRunContextReader"/> — evidence extraction from
/// <c>.relay/&lt;taskId&gt;/</c> run-artifact directories, including the new
/// verify-checks.json parsing and raw-tail fallback.
/// </summary>
public sealed class FailedRunContextReaderTests
{
    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "FailedRunContextReaderTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* best-effort */ }
    }

    // ── guard red ────────────────────────────────────────────────────────────

    [Fact]
    public void Read_GuardRed_IncludesGuardEvidence()
    {
        var dir = CreateTempDirectory();
        try
        {
            // Write NEEDS-REVIEW so we have a flag reason.
            File.WriteAllText(Path.Combine(dir, "NEEDS-REVIEW"),
                "flag reason line\nstage 10\n");

            // Write verify-checks.json with a red guard check and multi-line output.
            var checksJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["guardCheck"] = "red",
                ["guardOutput"] = "line1\nline2\nline3\nline4\nline5",
                ["testCheck"] = "green",
            });
            File.WriteAllText(
                Path.Combine(dir, "stage10-attempt1.verify-checks.json"), checksJson);

            // Write a verify-output.txt (content doesn't matter much for guard-red case
            // but we need the file to exist so the glob finds something).
            File.WriteAllText(
                Path.Combine(dir, "stage10-attempt1.verify-output.txt"),
                "# verify output (autopsy artifact)\n# check: green\nsome output\n");

            var ctx = FailedRunContextReader.Read(dir);

            Assert.Single(ctx.VerifyOutputs);
            var vo = ctx.VerifyOutputs[0];
            Assert.Equal(10, vo.Stage);
            Assert.Equal(1, vo.Attempt);
            Assert.Contains("guard check: red", vo.Summary, StringComparison.Ordinal);
            // The guard output tail should appear verbatim.
            Assert.Contains("line1", vo.Summary, StringComparison.Ordinal);
            Assert.Contains("line5", vo.Summary, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    // ── test red: [FAIL] lines preserved ─────────────────────────────────────

    [Fact]
    public void Read_TestRed_UsesExistingExtraction()
    {
        var dir = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(dir, "NEEDS-REVIEW"), "flag\nstage 9\n");

            // Write verify-checks.json with guard green, test red.
            var checksJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["guardCheck"] = "green",
                ["testCheck"] = "red",
                ["testExitCode"] = 1,
            });
            File.WriteAllText(
                Path.Combine(dir, "stage9-attempt1.verify-checks.json"), checksJson);

            // Write verify-output with [FAIL] lines and a tally.
            File.WriteAllText(
                Path.Combine(dir, "stage9-attempt1.verify-output.txt"),
                """
                # verify output (autopsy artifact)
                # check: red
                [FAIL] FirstTest.Fails
                [FAIL] SecondTest.Fails
                Some other output
                Failed: 2, Passed: 8
                """);

            var ctx = FailedRunContextReader.Read(dir);

            Assert.Single(ctx.VerifyOutputs);
            var vo = ctx.VerifyOutputs[0];
            Assert.Contains("[FAIL] FirstTest.Fails", vo.Summary, StringComparison.Ordinal);
            Assert.Contains("[FAIL] SecondTest.Fails", vo.Summary, StringComparison.Ordinal);
            Assert.Contains("Failed: 2, Passed: 8", vo.Summary, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    // ── no markers: fallback to raw tail ─────────────────────────────────────

    [Fact]
    public void Read_NoMarkers_FallsBackToRawTail()
    {
        var dir = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(dir, "NEEDS-REVIEW"), "flag\nstage 10\n");

            // No verify-checks.json — just a verify-output with no [FAIL] lines,
            // no Failed:/Passed: tally, no checks JSON to provide guard evidence.
            File.WriteAllText(
                Path.Combine(dir, "stage10-attempt1.verify-output.txt"),
                """
                # verify output (autopsy artifact)
                # check: red
                Some guard crash stack trace
                FileNotFoundException: System.Composition
                at SomeAssembly.Method()
                """);

            var ctx = FailedRunContextReader.Read(dir);

            Assert.Single(ctx.VerifyOutputs);
            var vo = ctx.VerifyOutputs[0];
            // Should NOT be empty — falls back to raw tail.
            Assert.NotEmpty(vo.Summary);
            Assert.Contains("guard crash", vo.Summary, StringComparison.Ordinal);
            Assert.Contains("FileNotFoundException", vo.Summary, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    // ── tail bounds: output truncated to 40 lines / 4000 chars ────────────────

    [Fact]
    public void Read_TailBounds_TruncatesToCap()
    {
        var dir = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(dir, "NEEDS-REVIEW"), "flag\nstage 10\n");

            // Create >40 lines of output with no [FAIL] markers.
            var lines = new List<string>
            {
                "# verify output (autopsy artifact)",
                "# check: red",
            };
            for (var i = 0; i < 100; i++)
                lines.Add($"line {i:000}: some diagnostic content here");
            File.WriteAllText(
                Path.Combine(dir, "stage10-attempt1.verify-output.txt"),
                string.Join('\n', lines));

            var ctx = FailedRunContextReader.Read(dir);

            Assert.Single(ctx.VerifyOutputs);
            var vo = ctx.VerifyOutputs[0];
            Assert.NotEmpty(vo.Summary);

            // The raw tail fallback caps at 40 lines. The summary should NOT
            // contain lines from the very beginning (e.g., line 000).
            Assert.DoesNotContain("line 000:", vo.Summary, StringComparison.Ordinal);
            // It should contain lines near the end.
            Assert.Contains("line 060:", vo.Summary, StringComparison.Ordinal);
            Assert.Contains("line 099:", vo.Summary, StringComparison.Ordinal);

            // Also verify the 4000-char cap: summary length ≤ 4,000.
            Assert.True(vo.Summary.Length <= 4_000);
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }
}
