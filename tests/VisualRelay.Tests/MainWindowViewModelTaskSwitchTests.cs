using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class MainWindowViewModelTaskSwitchTests
{
    /// <summary>
    /// Select a stage on task A, switch to task B: the same stage number
    /// must stay selected and StageDetail must reflect task B's artifacts.
    /// </summary>
    [Fact]
    public async Task SelectStage_ThenSwitchTask_KeepsStageSelectedAndUpdatesDetail()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");

        // Write stage-1 input artifacts for both tasks so StageDetail has
        // something to load.  Use different system prompts to tell them apart.
        WriteInputArtifact(repo, "alpha", 1, 1, systemPrompt: "Alpha system prompt",
            inputPrompt: "## Task input\nAlpha task\n\n## Prior stages\n\nContract.");
        WriteInputArtifact(repo, "beta", 1, 1, systemPrompt: "Beta system prompt",
            inputPrompt: "## Task input\nBeta task\n\n## Prior stages\n\nContract.");

        var vm = new MainWindowViewModel { RootPath = repo.Root };
        await vm.LoadInitialAsync();
        await vm.LastSelectionLoad!;

        // Select stage 1 on task alpha.
        vm.SelectStageCommand.Execute(vm.Stages[0]);
        Assert.True(vm.Stages[0].IsSelected);
        Assert.Equal("stage 01", vm.LogScopeLabel);
        Assert.Equal("Alpha system prompt", vm.StageDetail.SystemPromptText);
        Assert.Equal(StageDetailState.Ready, vm.StageDetail.SystemState);

        // Switch to task beta.
        vm.SelectedTask = vm.Tasks.Single(t => t.Id == "beta");
        await vm.LastSelectionLoad!;

        // Stage 1 must still be selected.
        Assert.True(vm.Stages[0].IsSelected);
        Assert.Equal("stage 01", vm.LogScopeLabel);

        // StageDetail must reflect beta's data (not alpha's).
        Assert.Equal("Beta system prompt", vm.StageDetail.SystemPromptText);
        Assert.Equal(StageDetailState.Ready, vm.StageDetail.SystemState);
    }

    /// <summary>
    /// Without selecting any stage, switching tasks must leave no stage
    /// selected and clear StageDetail via RefreshStageDetail(null).
    /// </summary>
    [Fact]
    public async Task NoStageSelected_SwitchTask_ClearsDetailAndNoStageSelected()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");

        WriteInputArtifact(repo, "alpha", 1, 1, systemPrompt: "Alpha prompt",
            inputPrompt: "## Task input\nAlpha\n\n## Prior stages\n\nContract.");
        WriteInputArtifact(repo, "beta", 1, 1, systemPrompt: "Beta prompt",
            inputPrompt: "## Task input\nBeta\n\n## Prior stages\n\nContract.");

        var vm = new MainWindowViewModel { RootPath = repo.Root };
        await vm.LoadInitialAsync();
        await vm.LastSelectionLoad!;

        // No stage selected — verify baseline.
        Assert.Equal("full", vm.LogScopeLabel);
        Assert.All(vm.Stages, s => Assert.False(s.IsSelected));

        // Switch to beta — still no stage selected.
        vm.SelectedTask = vm.Tasks.Single(t => t.Id == "beta");
        await vm.LastSelectionLoad!;

        Assert.Equal("full", vm.LogScopeLabel);
        Assert.All(vm.Stages, s => Assert.False(s.IsSelected));
        // No stage filter means RefreshStageDetail(null) → NoStage.
        Assert.Equal(StageDetailState.NoStage, vm.StageDetail.SystemState);
    }

    /// <summary>
    /// Selecting a stage then setting SelectedTask to null must clear the
    /// stage filter and reset StageDetail to NoStage.
    /// </summary>
    [Fact]
    public async Task SelectStage_ThenSetTaskToNull_ClearsFilterAndDetail()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        WriteInputArtifact(repo, "alpha", 1, 1, systemPrompt: "Alpha prompt",
            inputPrompt: "## Task input\nAlpha\n\n## Prior stages\n\nContract.");

        var vm = new MainWindowViewModel { RootPath = repo.Root };
        await vm.LoadInitialAsync();
        await vm.LastSelectionLoad!;

        // Select a stage — detail is populated.
        vm.SelectStageCommand.Execute(vm.Stages[0]);
        Assert.True(vm.Stages[0].IsSelected);
        Assert.Equal("stage 01", vm.LogScopeLabel);
        Assert.Equal(StageDetailState.Ready, vm.StageDetail.SystemState);

        // Deselect the task entirely.
        vm.SelectedTask = null;
        await vm.LastSelectionLoad!;

        // Stage filter must be cleared and detail reset.
        Assert.Equal("full", vm.LogScopeLabel);
        Assert.All(vm.Stages, s => Assert.False(s.IsSelected));
        Assert.Equal(StageDetailState.NoStage, vm.StageDetail.SystemState);
        Assert.Empty(vm.StageDetail.Header);
    }

    /// <summary>
    /// After switching tasks with a stage selected, toggling the stage
    /// off then back on must still work correctly (SelectStage command
    /// still functions).
    /// </summary>
    [Fact]
    public async Task SwitchTask_ThenToggleStage_WorksCorrectly()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");

        WriteInputArtifact(repo, "alpha", 1, 1, systemPrompt: "Alpha prompt",
            inputPrompt: "## Task input\nAlpha\n\n## Prior stages\n\nContract.");
        WriteInputArtifact(repo, "beta", 1, 1, systemPrompt: "Beta prompt",
            inputPrompt: "## Task input\nBeta\n\n## Prior stages\n\nContract.");

        var vm = new MainWindowViewModel { RootPath = repo.Root };
        await vm.LoadInitialAsync();
        await vm.LastSelectionLoad!;

        // Select stage 1 on alpha.
        vm.SelectStageCommand.Execute(vm.Stages[0]);

        // Switch to beta — stage 1 stays selected.
        vm.SelectedTask = vm.Tasks.Single(t => t.Id == "beta");
        await vm.LastSelectionLoad!;
        Assert.True(vm.Stages[0].IsSelected);
        Assert.Equal("Beta prompt", vm.StageDetail.SystemPromptText);

        // Toggle stage 1 off — should clear the filter and detail.
        vm.SelectStageCommand.Execute(vm.Stages[0]);
        Assert.False(vm.Stages[0].IsSelected);
        Assert.Equal("full", vm.LogScopeLabel);
        Assert.Equal(StageDetailState.NoStage, vm.StageDetail.SystemState);

        // Toggle stage 1 back on — should re-select and reload from beta.
        vm.SelectStageCommand.Execute(vm.Stages[0]);
        Assert.True(vm.Stages[0].IsSelected);
        Assert.Equal("stage 01", vm.LogScopeLabel);
        Assert.Equal("Beta prompt", vm.StageDetail.SystemPromptText);
    }

    /// <summary>
    /// Select a stage on task A, switch to task B that has NO artifacts
    /// for that stage: StageDetail must still load gracefully (NotStarted
    /// or fallback) and the stage must stay selected.
    /// </summary>
    [Fact]
    public async Task SelectStage_SwitchToTaskWithoutArtifacts_StageSelectedDetailFallback()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");

        // Only alpha has stage-1 artifacts; beta does not.
        WriteInputArtifact(repo, "alpha", 1, 1, systemPrompt: "Alpha prompt",
            inputPrompt: "## Task input\nAlpha\n\n## Prior stages\n\nContract.");

        var vm = new MainWindowViewModel { RootPath = repo.Root };
        await vm.LoadInitialAsync();
        await vm.LastSelectionLoad!;

        vm.SelectStageCommand.Execute(vm.Stages[0]);
        Assert.Equal("Alpha prompt", vm.StageDetail.SystemPromptText);

        // Switch to beta — no input artifact for stage 1.
        vm.SelectedTask = vm.Tasks.Single(t => t.Id == "beta");
        await vm.LastSelectionLoad!;

        // Stage 1 is still selected.
        Assert.True(vm.Stages[0].IsSelected);
        // StageDetail loads the static system prompt as fallback (Ready state).
        Assert.Equal(StageDetailState.Ready, vm.StageDetail.SystemState);
        Assert.NotEmpty(vm.StageDetail.SystemPromptText);
    }

    private static void WriteInputArtifact(TestRepository repo, string taskId,
        int stage, int attempt, string systemPrompt, string inputPrompt)
    {
        var taskDir = Path.Combine(repo.Root, ".relay", taskId);
        Directory.CreateDirectory(taskDir);
        var reportPath = Path.Combine(taskDir,
            $"stage{stage}-attempt{attempt}.report.json");
        StageInputArtifact.Write(reportPath, new StageInputArtifact(
            1, stage, attempt, "Name", systemPrompt, inputPrompt,
            "2026-06-20T19:00:00Z"));
    }
}
