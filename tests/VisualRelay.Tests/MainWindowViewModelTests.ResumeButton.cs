using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

public sealed partial class MainWindowViewModelTests
{
    [Fact]
    public void ResumeSelectedCommand_CanExecute_False_WhenNoTaskSelected()
    {
        var vm = new MainWindowViewModel();
        Assert.False(vm.ResumeSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void ResumeSelectedCommand_CanExecute_True_WhenNonArchivedTaskSelected()
    {
        var vm = new MainWindowViewModel();
        vm.Tasks.Add(new TaskRowViewModel(new("alpha", "/tmp/llm-tasks/alpha.md", "/tmp/llm-tasks", false, [])));
        vm.SelectedTask = vm.Tasks[0];

        Assert.True(vm.ResumeSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void ResumeSelectedCommand_CanExecute_False_WhenArchivedTaskSelected()
    {
        var vm = new MainWindowViewModel();
        vm.Tasks.Add(new TaskRowViewModel(new("alpha", "/tmp/llm-tasks/alpha.md", "/tmp/llm-tasks", false, [],
            IsArchived: true)));
        vm.ShowArchive = true;
        vm.SelectedTask = vm.Tasks[0];

        Assert.False(vm.ResumeSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void ResumeSelectedCommand_CanExecute_False_WhenBusy()
    {
        var vm = new MainWindowViewModel();
        vm.Tasks.Add(new TaskRowViewModel(new("alpha", "/tmp/llm-tasks/alpha.md", "/tmp/llm-tasks", false, [])));
        vm.SelectedTask = vm.Tasks[0];
        vm.IsBusy = true;

        Assert.False(vm.ResumeSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void ResumeSelectedCommand_CanExecute_False_WhenPaused()
    {
        var vm = new MainWindowViewModel();
        vm.Tasks.Add(new TaskRowViewModel(new("alpha", "/tmp/llm-tasks/alpha.md", "/tmp/llm-tasks", false, [])));
        vm.SelectedTask = vm.Tasks[0];
        vm.PauseRequested = true;

        Assert.False(vm.ResumeSelectedCommand.CanExecute(null));
    }
}
