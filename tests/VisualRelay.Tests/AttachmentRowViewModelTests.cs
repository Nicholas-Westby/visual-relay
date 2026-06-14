using CommunityToolkit.Mvvm.Input;
using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

public sealed class AttachmentRowViewModelTests
{
    [Fact]
    public void Constructor_StoresPathAndCommands()
    {
        var revealCmd = new RelayCommand(() => { });
        var removeCmd = new RelayCommand(() => { });
        var row = new AttachmentRowViewModel("/path/to/file.txt", revealCmd, removeCmd);

        Assert.Equal("/path/to/file.txt", row.Path);
        Assert.Same(revealCmd, row.RevealCommand);
        Assert.Same(removeCmd, row.RemoveCommand);
    }

    [Fact]
    public async Task MainWindowViewModel_Attachments_EmptyByDefault()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("no-attachments", "# No attachments\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };

        await viewModel.LoadInitialAsync();

        Assert.Empty(viewModel.Attachments);
    }

    [Fact]
    public async Task MainWindowViewModel_Attachments_ProjectedFromSelectedTaskSiblingPaths()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteNestedTask(
            "beta", "# Beta\n",
            ("info.txt", "some info"),
            ("data.json", "{}"));
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };

        await viewModel.LoadInitialAsync();
        viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "beta");

        // After selecting a task with sibling paths, Attachments is rebuilt.
        Assert.NotEmpty(viewModel.Attachments);
        Assert.Equal(2, viewModel.Attachments.Count);

        var paths = viewModel.Attachments.Select(a => a.Path).ToList();
        Assert.Contains(paths, p => p.EndsWith("info.txt", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.EndsWith("data.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MainWindowViewModel_AttachmentRows_HaveNonNullCommands()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask(
            "gamma", "# Gamma\n",
            ("readme.md", "# Readme"));
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };

        await viewModel.LoadInitialAsync();
        viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "gamma");

        var row = Assert.Single(viewModel.Attachments);
        Assert.NotNull(row.RevealCommand);
        Assert.NotNull(row.RemoveCommand);
    }

    [Fact]
    public async Task MainWindowViewModel_Attachments_ClearedWhenSelectingTaskWithoutSiblings()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask(
            "with-siblings", "# With siblings\n",
            ("extra.log", "log content"));
        repo.WriteTask("no-siblings", "# No siblings\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };

        await viewModel.LoadInitialAsync();
        viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "with-siblings");
        Assert.NotEmpty(viewModel.Attachments);

        viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "no-siblings");

        Assert.Empty(viewModel.Attachments);
    }

    [Fact]
    public async Task MainWindowViewModel_Attachments_ClearedWhenSelectingNullTask()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask(
            "delta", "# Delta\n",
            ("notes.txt", "notes"));
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };

        await viewModel.LoadInitialAsync();
        viewModel.SelectedTask = viewModel.Tasks.Single(t => t.Id == "delta");
        Assert.NotEmpty(viewModel.Attachments);

        viewModel.SelectedTask = null;

        Assert.Empty(viewModel.Attachments);
    }

    [Fact]
    public void AttachmentRowViewModel_InheritsFromViewModelBase()
    {
        var row = new AttachmentRowViewModel(
            "test.txt",
            new RelayCommand(() => { }),
            new RelayCommand(() => { }));

        Assert.IsAssignableFrom<ViewModelBase>(row);
    }

    [Fact]
    public void RevealCommand_PropertyType_IsIRelayCommand()
    {
        var propType = typeof(AttachmentRowViewModel).GetProperty(
            nameof(AttachmentRowViewModel.RevealCommand))!.PropertyType;
        Assert.Equal(typeof(IRelayCommand), propType);
    }

    [Fact]
    public void RemoveCommand_PropertyType_IsIRelayCommand()
    {
        var propType = typeof(AttachmentRowViewModel).GetProperty(
            nameof(AttachmentRowViewModel.RemoveCommand))!.PropertyType;
        Assert.Equal(typeof(IRelayCommand), propType);
    }

    [Fact]
    public void Constructor_ParameterTypes_AreIRelayCommand()
    {
        var ctor = typeof(AttachmentRowViewModel).GetConstructors().Single();
        var parameters = ctor.GetParameters();
        var revealParam = parameters.Single(p => p.Name == "revealCommand");
        var removeParam = parameters.Single(p => p.Name == "removeCommand");
        Assert.Equal(typeof(IRelayCommand), revealParam.ParameterType);
        Assert.Equal(typeof(IRelayCommand), removeParam.ParameterType);
    }
}
