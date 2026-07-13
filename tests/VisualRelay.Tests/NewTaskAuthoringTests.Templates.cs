using Avalonia.Threading;
using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

public sealed partial class NewTaskAuthoringTests
{
    [AvaloniaFact]
    public void OpenNewTaskDialog_ListsBuiltInsWithBlankSelected()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        viewModel.OpenNewTaskDialogCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["Blank", "Create Tasks to Speed Up Automated Tests"], viewModel.NewTaskTemplateNames);

        // Blank must be the default selection.
        var blankIdx = viewModel.SelectedNewTaskTemplateIndex;
        Assert.True(blankIdx >= 0);
        Assert.Equal("Blank", viewModel.NewTaskTemplateNames[blankIdx]);

        Assert.Equal(string.Empty, viewModel.NewTaskTitle);
        Assert.Equal(string.Empty, viewModel.NewTaskBody);
    }

    [AvaloniaFact]
    public void SelectingSpeedUpTemplate_PrefillsTitleAndBody()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        viewModel.OpenNewTaskDialogCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // Find the speed-up index.
        var speedUpIdx = -1;
        for (var i = 0; i < viewModel.NewTaskTemplateNames.Count; i++)
        {
            if (viewModel.NewTaskTemplateNames[i] == "Create Tasks to Speed Up Automated Tests")
            {
                speedUpIdx = i;
                break;
            }
        }

        Assert.True(speedUpIdx >= 0, "Speed-up template must be in the list");

        // Select speed-up.
        viewModel.SelectedNewTaskTemplateIndex = speedUpIdx;

        Assert.Equal("Speed up automated tests", viewModel.NewTaskTitle);
        Assert.Contains("- test time dropped from 80s to 60s, saving 20s",
            viewModel.NewTaskBody, StringComparison.Ordinal);

        // Re-select Blank — both fields should clear.
        var blankIdx = viewModel.SelectedNewTaskTemplateIndex; // should be 0
        // Need to change the index to fire the hook. Reset to blank.
        for (var i = 0; i < viewModel.NewTaskTemplateNames.Count; i++)
        {
            if (viewModel.NewTaskTemplateNames[i] == "Blank")
            {
                blankIdx = i;
                break;
            }
        }

        viewModel.SelectedNewTaskTemplateIndex = blankIdx;
        Assert.Equal(string.Empty, viewModel.NewTaskTitle);
        Assert.Equal(string.Empty, viewModel.NewTaskBody);
    }

    [AvaloniaFact]
    public void TemplateChange_NeverClobbersUserEditedField()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        viewModel.OpenNewTaskDialogCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // Type a custom body.
        viewModel.NewTaskBody = "My custom body.";

        // Now select speed-up template.
        var speedUpIdx = -1;
        for (var i = 0; i < viewModel.NewTaskTemplateNames.Count; i++)
        {
            if (viewModel.NewTaskTemplateNames[i] == "Create Tasks to Speed Up Automated Tests")
            {
                speedUpIdx = i;
                break;
            }
        }

        Assert.True(speedUpIdx >= 0);
        viewModel.SelectedNewTaskTemplateIndex = speedUpIdx;

        // Body must keep the custom text — NOT clobbered by template.
        Assert.Equal("My custom body.", viewModel.NewTaskBody);
        // Title however was empty, so it gets the template title.
        Assert.Equal("Speed up automated tests", viewModel.NewTaskTitle);
    }

    [AvaloniaFact]
    public void RepoTemplate_OverridesBuiltInBlank()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        // Write a repo-level blank template override.
        var templatesDir = Path.Combine(repo.Root, "llm-tasks", "templates");
        Directory.CreateDirectory(templatesDir);
        File.WriteAllText(Path.Combine(templatesDir, "blank.md"), "---\nname: Blank\n---\nrepo skeleton\n");

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        viewModel.OpenNewTaskDialogCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // Blank must still be first and default.
        Assert.Equal("Blank", viewModel.NewTaskTemplateNames[0]);
        Assert.Equal(0, viewModel.SelectedNewTaskTemplateIndex);

        // Body must come from repo, not built-in (which is empty).
        Assert.Equal("repo skeleton\n", viewModel.NewTaskBody);
    }

    [AvaloniaFact]
    public void UserTemplate_AppearsInDropdown()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        // TestRepository sets XDG_CONFIG_HOME=repo.Root, so the user
        // templates dir resolves to <repo.Root>/visual-relay/templates.
        var userDir = Path.Combine(repo.Root, "visual-relay", "templates");
        Directory.CreateDirectory(userDir);
        File.WriteAllText(Path.Combine(userDir, "deploy-checklist.md"), "---\nname: Deploy Checklist\n---\n# Deploy\n");

        var viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        viewModel.OpenNewTaskDialogCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("Deploy Checklist", viewModel.NewTaskTemplateNames);

        // It must be after Blank alphabetically.
        var blankIdx = viewModel.NewTaskTemplateNames.IndexOf("Blank");
        var checklistIdx = viewModel.NewTaskTemplateNames.IndexOf("Deploy Checklist");
        Assert.True(checklistIdx > blankIdx, "Deploy Checklist must sort after Blank");

        // Select it — prefill should work.
        viewModel.SelectedNewTaskTemplateIndex = checklistIdx;
        Assert.Contains("# Deploy", viewModel.NewTaskBody, StringComparison.Ordinal);
    }
}
