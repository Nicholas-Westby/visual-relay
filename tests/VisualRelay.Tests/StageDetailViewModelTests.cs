using System.Text.Json;
using VisualRelay.App.Services;
using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class StageDetailViewModelTests
{
    private static StageRowViewModel MakeStage(int n, string name, string tier) =>
        new(new RelayStageDefinition(n, name, tier, "llm", "all", "all",
            $"System prompt for {name}", "Contract line"));

    private static StageRowViewModel MakeDriverStage() => new(RelayStages.All[10]);

    [Fact]
    public void Load_NullStage_AllStatesNoStage()
    {
        var vm = new StageDetailViewModel();
        vm.Load(null, "/some/dir");
        AssertAllStates(vm, StageDetailState.NoStage);
        Assert.Empty(vm.SystemPromptText);
        Assert.Empty(vm.Header);
    }

    [Fact]
    public void Load_ValidStageNullDirectory_ShowsStaticSystemPrompt_InputNotStarted_OutputNotComplete()
    {
        var vm = new StageDetailViewModel();
        var stage = MakeStage(1, "Ideate", "cheap");
        vm.Load(stage, null);
        Assert.Equal(StageDetailState.Ready, vm.SystemState);
        Assert.Equal(RelayStages.All[0].SystemPrompt, vm.SystemPromptText);
        Assert.Equal(StageDetailState.NotStarted, vm.InputState);
        Assert.Equal(StageDetailState.NotComplete, vm.OutputState);
        Assert.Contains("Stage 01 (Ideate)", vm.Header);
        Assert.DoesNotContain("attempt", vm.Header);
    }

    [Fact]
    public void Load_ValidStageNonexistentDirectory_ShowsStaticSystemPrompt_InputNotStarted_OutputNotComplete()
    {
        var vm = new StageDetailViewModel();
        var stage = MakeStage(1, "Ideate", "cheap");
        vm.Load(stage, "/nonexistent/path/12345");
        Assert.Equal(StageDetailState.Ready, vm.SystemState);
        Assert.Equal(RelayStages.All[0].SystemPrompt, vm.SystemPromptText);
        Assert.Equal(StageDetailState.NotStarted, vm.InputState);
        Assert.Equal(StageDetailState.NotComplete, vm.OutputState);
        Assert.Contains("Stage 01 (Ideate)", vm.Header);
        Assert.DoesNotContain("attempt", vm.Header);
    }

    [Fact]
    public void Load_DriverStage_AllStatesDriverStage()
    {
        using var dir = new TempDirectory();
        var vm = new StageDetailViewModel();
        vm.Load(MakeDriverStage(), dir.Path);
        AssertAllStates(vm, StageDetailState.DriverStage);
        Assert.Contains("Commit", vm.Header);
    }

    [Fact]
    public void Load_NoInputFiles_FallbackSystemPrompt_InputNotStarted_OutputNotComplete()
    {
        using var dir = new TempDirectory();
        var vm = new StageDetailViewModel();
        vm.Load(MakeStage(5, "Author-tests", "balanced"), dir.Path);
        Assert.Equal(StageDetailState.Ready, vm.SystemState);
        Assert.Equal(RelayStages.All[4].SystemPrompt, vm.SystemPromptText);
        Assert.Equal(StageDetailState.NotStarted, vm.InputState);
        Assert.Equal(StageDetailState.NotComplete, vm.OutputState);
    }

    [Fact]
    public void Load_PersistedSystemPrompt_UsedOverStatic()
    {
        using var dir = new TempDirectory();
        WriteInputArtifact(dir, 5, 1, systemPrompt: "Custom system prompt from file.",
            inputPrompt: "## Task input\nDo the thing.\n\n## Prior stages\nledger\n\nContract line.");

        var vm = new StageDetailViewModel();
        vm.Load(MakeStage(5, "Author-tests", "balanced"), dir.Path);
        Assert.Equal("Custom system prompt from file.", vm.SystemPromptText);
    }

    [Fact]
    public void Load_InputFilePresent_InputStateReady_WithParsedSections()
    {
        using var dir = new TempDirectory();
        WriteInputArtifact(dir, 5, 1, systemPrompt: "Write tests.", inputPrompt: string.Join('\n',
            "# Relay stage 5: Author-tests", "Task: test-task", "Working directory: /tmp",
            "", "## Task input", "Write failing tests.",
            "", "## Manifest", "+tests/MyTests.cs",
            "", "## Prior stages", "## Stage 1 - Ideate", "previous output",
            "", "Contract goes here."));

        var vm = new StageDetailViewModel();
        vm.Load(MakeStage(5, "Author-tests", "balanced"), dir.Path);
        Assert.Equal(StageDetailState.Ready, vm.InputState);
        Assert.NotEmpty(vm.InputSections);
        Assert.Contains(vm.InputSections, s => s.Title == "Output contract");
    }

    [Fact]
    public void Load_ReportPresent_OutputStateReady_WithFields()
    {
        using var dir = new TempDirectory();
        WriteInputArtifact(dir, 5, 1, systemPrompt: "Write tests.",
            inputPrompt: "## Task input\nhello\n\n## Prior stages\nledger\n\nContract.");
        WriteReport(dir, 5, 1, """{"testFiles": ["a.cs"], "rationale": "need tests"}""");

        var vm = new StageDetailViewModel();
        vm.Load(MakeStage(5, "Author-tests", "balanced"), dir.Path);
        Assert.Equal(StageDetailState.Ready, vm.OutputState);
        Assert.NotEmpty(vm.OutputFields);
        var tf = Assert.Single(vm.OutputFields, f => f.Label == "testFiles");
        Assert.Equal(OutputFieldKind.List, tf.Kind);
        Assert.Equal("a.cs", tf.Value);
    }

    [Fact]
    public void Load_ReportWithFencedJsonInAnswer_ExtractsAndParses()
    {
        using var dir = new TempDirectory();
        WriteInputArtifact(dir, 1, 1, systemPrompt: "Frame the task.",
            inputPrompt: "## Task input\nhello\n\n## Prior stages\nledger\n\nContract.");
        WriteReport(dir, 1, 1, """
            Some text before.
            ```json
            {"summary": "framed idea", "options": ["opt-a", "opt-b"]}
            ```
            Some text after.
            """);

        var vm = new StageDetailViewModel();
        vm.Load(MakeStage(1, "Ideate", "cheap"), dir.Path);
        Assert.Equal(StageDetailState.Ready, vm.OutputState);
        Assert.Equal("framed idea",
            Assert.Single(vm.OutputFields, f => f.Label == "summary").Value);
        Assert.Equal("opt-a\nopt-b",
            Assert.Single(vm.OutputFields, f => f.Label == "options").Value);
    }

    [Fact]
    public void Load_LatestAttemptByNumber_NotMtime()
    {
        using var dir = new TempDirectory();
        WriteInputArtifact(dir, 5, 1, systemPrompt: "Attempt 1.", inputPrompt: "## Task input\na1\n\n## Prior stages\n\nContract.");
        WriteInputArtifact(dir, 5, 3, systemPrompt: "Attempt 3 — LATEST.", inputPrompt: "## Task input\na3\n\n## Prior stages\n\nContract.");
        WriteInputArtifact(dir, 5, 2, systemPrompt: "Attempt 2.", inputPrompt: "## Task input\na2\n\n## Prior stages\n\nContract.");
        // Force attempt 3 mtime older than attempt 2
        var path3 = Path.Combine(dir.Path, "stage5-attempt3.input.json");
        File.SetLastWriteTimeUtc(path3, File.GetLastWriteTimeUtc(path3).AddDays(-1));

        var vm = new StageDetailViewModel();
        vm.Load(MakeStage(5, "Author-tests", "balanced"), dir.Path);
        Assert.Equal("Attempt 3 — LATEST.", vm.SystemPromptText);
    }

    [Fact]
    public void Load_ReportLatestAttemptByNumber_NotMtime()
    {
        using var dir = new TempDirectory();
        WriteInputArtifact(dir, 5, 1, "Prompt.", "## Task input\nx\n\n## Prior stages\n\nContract.");
        WriteInputArtifact(dir, 5, 2, "Prompt.", "## Task input\nx\n\n## Prior stages\n\nContract.");
        WriteReport(dir, 5, 1, """{"verdict":"old"}""");
        var r2 = WriteReport(dir, 5, 2, """{"verdict":"latest_by_number"}""");
        File.SetLastWriteTimeUtc(r2, File.GetLastWriteTimeUtc(
            Path.Combine(dir.Path, "stage5-attempt1.report.json")).AddHours(-1));

        var vm = new StageDetailViewModel();
        vm.Load(MakeStage(5, "Author-tests", "balanced"), dir.Path);
        Assert.Equal("latest_by_number",
            Assert.Single(vm.OutputFields, f => f.Label == "verdict").Value);
    }

    [Fact]
    public void Load_HeaderFormat_IncludesStageAndAttemptAndSize()
    {
        using var dir = new TempDirectory();
        WriteInputArtifact(dir, 6, 2, systemPrompt: "Implement.", inputPrompt: new string('x', 2048));

        var vm = new StageDetailViewModel();
        vm.Load(MakeStage(6, "Implement", "balanced"), dir.Path);
        Assert.Contains("Stage 06 (Implement)", vm.Header);
        Assert.Contains("attempt 2", vm.Header);
        Assert.Contains("KB", vm.Header);
    }

    [Fact]
    public void Load_NoInputFile_HeaderOmitsAttemptAndSize()
    {
        using var dir = new TempDirectory();
        var vm = new StageDetailViewModel();
        vm.Load(MakeStage(3, "Diagnose", "balanced"), dir.Path);
        Assert.Contains("Stage 03 (Diagnose)", vm.Header);
        Assert.DoesNotContain("attempt", vm.Header);
    }

    [Fact]
    public void Load_ClearingStage_ResetsToNoStage()
    {
        using var dir = new TempDirectory();
        WriteInputArtifact(dir, 1, 1, "Frame.", "## Task input\nhello\n\n## Prior stages\n\nContract.");
        var vm = new StageDetailViewModel();
        vm.Load(MakeStage(1, "Ideate", "cheap"), dir.Path);
        Assert.Equal(StageDetailState.Ready, vm.SystemState);
        vm.Load(null, dir.Path);
        AssertAllStates(vm, StageDetailState.NoStage);
    }

    [Fact]
    public void Load_StageDoneNoReport_OutputStateSkipped()
    {
        // When a stage has Status="Done" (e.g. Fix-verify skipped because
        // Verify found no issues) but no report file exists, the output tab
        // should show the Skipped state — not the misleading NotComplete
        // "will appear once the stage completes" message.
        using var dir = new TempDirectory();
        var stage = MakeStage(10, "Fix-verify", "balanced");
        stage.Status = "Done";

        var vm = new StageDetailViewModel();
        vm.Load(stage, dir.Path);

        Assert.Equal(StageDetailState.Skipped, vm.OutputState);
        Assert.True(vm.IsOutputSkipped);
        // System prompt still loads from the static definition.
        Assert.Equal(StageDetailState.Ready, vm.SystemState);
        // Input has no artifact file, so it remains NotStarted.
        Assert.Equal(StageDetailState.NotStarted, vm.InputState);
    }

    [Fact]
    public void Load_StageNotDoneNoReport_OutputStateNotComplete()
    {
        // When a stage has Status != "Done" (e.g. still Waiting/Running) and
        // no report file exists, the output is genuinely NotComplete — not
        // Skipped. This guards against accidentally treating all missing-report
        // cases as skipped.
        using var dir = new TempDirectory();
        var stage = MakeStage(10, "Fix-verify", "balanced");
        stage.Status = "Waiting";

        var vm = new StageDetailViewModel();
        vm.Load(stage, dir.Path);

        Assert.Equal(StageDetailState.NotComplete, vm.OutputState);
        Assert.False(vm.IsOutputSkipped);
    }

    private static void AssertAllStates(StageDetailViewModel vm, StageDetailState expected)
    {
        Assert.Equal(expected, vm.SystemState);
        Assert.Equal(expected, vm.InputState);
        Assert.Equal(expected, vm.OutputState);
    }

    private static void WriteInputArtifact(TempDirectory dir, int stage, int attempt,
        string systemPrompt, string inputPrompt)
    {
        var reportPath = Path.Combine(dir.Path, $"stage{stage}-attempt{attempt}.report.json");
        StageInputArtifact.Write(reportPath, new StageInputArtifact(
            1, stage, attempt, "Name", systemPrompt, inputPrompt, "2026-06-20T19:00:00Z"));
    }

    private static string WriteReport(TempDirectory dir, int stage, int attempt, string answerJson)
    {
        var path = Path.Combine(dir.Path, $"stage{stage}-attempt{attempt}.report.json");
        var report = JsonSerializer.Serialize(new { result = new { answer = answerJson } });
        File.WriteAllText(path, report);
        return path;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "vr-sdvm-tests", Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { TestFileSystem.DeleteDirectoryResilient(Path); }
            catch
            {
                // Best-effort cleanup; ignore failures to avoid masking test results.
            }
        }
    }
}
