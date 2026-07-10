using System.Text.Json;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class SetupCheckResultsTests
{
    // ── Unit: artifact produced on failure, null on pass ────────────────

    [Fact]
    public async Task FailingValidation_NonZeroNoOutput_ProducesArtifactWithAllFields()
    {
        using var repo = TestRepository.Create();
        // Need a toolchain marker so the detector finds a candidate to validate.
        File.WriteAllText(Path.Combine(repo.Root, "go.mod"), "module m\n\ngo 1.22\n");
        // Exit 127 with no output → rejected (command not found).
        var failingRunner = new ScriptedTestRunner(new TestRunResult(127, ""));

        var result = await ProjectBootstrapper.BootstrapAsync(repo.Root, validationRunner: failingRunner);

        Assert.True(result.UsedPlaceholderTestCommand);
        Assert.NotNull(result.SetupCheck);
        Assert.Equal(127, result.SetupCheck.ExitCode);
        Assert.False(result.SetupCheck.TimedOut);
        Assert.Equal(repo.Root, result.SetupCheck.Cwd);
        // OutputTail may be null if there's no output
        Assert.NotNull(result.SetupCheck.ArtifactPath);
        Assert.True(File.Exists(result.SetupCheck.ArtifactPath));

        // Artifact on disk has the diagnostic fields.
        var artifact = await File.ReadAllTextAsync(result.SetupCheck.ArtifactPath!);
        Assert.Contains("capturedUtc:", artifact);
        Assert.Contains("exitCode: 127", artifact);
        Assert.Contains("timedOut: False", artifact);
    }

    [Fact]
    public async Task FailingValidation_Timeout_ProducesArtifact()
    {
        using var repo = TestRepository.Create();
        // Need a toolchain marker so the detector finds a candidate to validate.
        File.WriteAllText(Path.Combine(repo.Root, "go.mod"), "module m\n\ngo 1.22\n");
        var timedOut = new TestRunResult(-1, "partial output before kill\n", TimedOut: true);
        var failingRunner = new ScriptedTestRunner(timedOut);

        var result = await ProjectBootstrapper.BootstrapAsync(repo.Root, validationRunner: failingRunner,
            validationTimeoutMs: 5000);

        Assert.NotNull(result.SetupCheck);
        Assert.True(result.SetupCheck.TimedOut);
        Assert.Equal(-1, result.SetupCheck.ExitCode);
        Assert.Contains("partial output", result.SetupCheck.OutputTail);
        Assert.Contains("5000", result.SetupCheck.ArtifactPath != null
            ? await File.ReadAllTextAsync(result.SetupCheck.ArtifactPath)
            : "missing");
    }

    [Fact]
    public async Task PassingValidation_ClearsSetupCheck()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "go.mod"), "module m\n\ngo 1.22\n");
        var accepting = new ScriptedTestRunner(new TestRunResult(0, "ok"));

        var result = await ProjectBootstrapper.BootstrapAsync(repo.Root, validationRunner: accepting);

        Assert.Null(result.SetupCheck);
        Assert.False(result.UsedPlaceholderTestCommand);
    }

    [Fact]
    public async Task MultiCandidateFailure_PersistsOneSectionPerCandidate()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "go.mod"), "module m\n\ngo 1.22\n");
        File.WriteAllText(Path.Combine(repo.Root, "package.json"),
            "{\"scripts\":{\"test\":\"vitest run\"}}");
        // Both candidates time out — every rejected candidate is recorded.
        var alwaysFails = new ScriptedTestRunner(
            new TestRunResult(-1, "npm timed out", TimedOut: true),
            new TestRunResult(-1, "go timed out", TimedOut: true));
        var result = await ProjectBootstrapper.BootstrapAsync(repo.Root, validationRunner: alwaysFails,
            validationTimeoutMs: 10000);

        Assert.NotNull(result.SetupCheck);
        Assert.NotNull(result.SetupCheck.OutputTail);
        Assert.NotNull(result.SetupCheck.ArtifactPath);
        var artifact = await File.ReadAllTextAsync(result.SetupCheck.ArtifactPath!);
        Assert.Contains("Candidate 1", artifact);
        Assert.Contains("Candidate 2", artifact);
        Assert.Contains("npm timed out", artifact);
        Assert.Contains("go timed out", artifact);
    }

    // ── Control API /state test ─────────────────────────────────────────

    [Fact]
    public async Task ControlApiState_AfterFailedSetupCheck_ContainsStructuredSetupCheck()
    {
        using var repo = TestRepository.Create();
        // Need a toolchain marker so the detector finds a candidate to validate.
        File.WriteAllText(Path.Combine(repo.Root, "go.mod"), "module m\n\ngo 1.22\n");
        var output = "some stderr\nfrom failing command\n";
        var failing = new ScriptedTestRunner(new TestRunResult(1, output, TimedOut: true));
        var result = await ProjectBootstrapper.BootstrapAsync(repo.Root, validationRunner: failing,
            validationTimeoutMs: 15000);

        Assert.NotNull(result.SetupCheck);
        // Simulate the /state JSON shape.
        var sc = result.SetupCheck;
        var state = new
        {
            setupCheck = new
            {
                command = sc.Command,
                cwd = sc.Cwd,
                timeoutMs = sc.TimeoutMs,
                exitCode = sc.ExitCode,
                timedOut = sc.TimedOut,
                outputTail = sc.OutputTail,
                artifactPath = sc.ArtifactPath,
                capturedUtc = sc.CapturedUtc,
                hint = sc.Hint
            }
        };

        var json = JsonSerializer.Serialize(state);
        Assert.Contains("exitCode", json);
        Assert.Contains("\"command\"", json);
        Assert.Contains("\"cwd\"", json);
        Assert.Contains("\"timeoutMs\"", json);
        Assert.Contains("\"timedOut\"", json);
        Assert.Contains("\"outputTail\"", json);
        Assert.Contains("\"artifactPath\"", json);
        Assert.Contains("\"hint\"", json);
        Assert.Contains("some stderr", json);
    }

    [Fact]
    public async Task ControlApiState_AfterPassingSetupCheck_SetupCheckIsNull()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "go.mod"), "module m\n\ngo 1.22\n");
        var accepting = new ScriptedTestRunner(new TestRunResult(0, "ok"));
        var result = await ProjectBootstrapper.BootstrapAsync(repo.Root, validationRunner: accepting);

        Assert.Null(result.SetupCheck);
    }

    // ── Hint derivation ─────────────────────────────────────────────────

    [Fact]
    public void Hint_Timeout_AdvisesRaisingTimeout()
    {
        var diag = new SetupCheckDiagnostic("long-running-cmd", "/tmp", 5000, -1, true,
            "partial\n", "/tmp/setup-check.log", DateTimeOffset.UtcNow);
        Assert.Contains("exceeded 5s", diag.Hint);
        Assert.Contains("raise testTimeoutMs", diag.Hint);
    }

    [Fact]
    public void Hint_Exit127_AdvisesPath()
    {
        var diag = new SetupCheckDiagnostic("nonexistent-bin", "/tmp", 60000, 127, false,
            "sh: nonexistent-bin: not found\n", null, DateTimeOffset.UtcNow);
        Assert.Contains("not found on VR's PATH", diag.Hint);
    }

    [Fact]
    public void Hint_GenericExit_ReportsCode()
    {
        var diag = new SetupCheckDiagnostic("dotnet test", "/tmp", 60000, 2, false,
            "FAIL\n", null, DateTimeOffset.UtcNow);
        Assert.Contains("exited with code 2", diag.Hint);
    }

    // ── Output tail capping ─────────────────────────────────────────────

    [Fact]
    public void CapForState_TruncatesLongOutput()
    {
        var longStr = new string('x', 8000);
        var capped = SetupCheckDiagnostic.CapForState(longStr);
        Assert.NotNull(capped);
        Assert.True(capped.Length <= 5000); // generous bound; StateTailCapChars is 4096 + marker
        Assert.StartsWith("[…truncated…]", capped);
    }

    [Fact]
    public void CapForState_ShortOutput_ReturnsUnchanged()
    {
        const string shortStr = "hello";
        var capped = SetupCheckDiagnostic.CapForState(shortStr);
        Assert.Equal(shortStr, capped);
    }

    [Fact]
    public void CapForState_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(SetupCheckDiagnostic.CapForState(null));
        Assert.Null(SetupCheckDiagnostic.CapForState(""));
    }
}
