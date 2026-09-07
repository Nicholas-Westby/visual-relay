using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Cancel has to reach the run itself, not just the button state: the token the
/// single-task run hands its driver is the one the cancel command cancels, and the
/// status line afterwards names the task the operator stopped.
/// </summary>
public sealed class MainWindowViewModelCancelTests
{
    [Fact]
    public async Task CancelRun_DuringASingleTaskRun_CancelsTheTokenTheRunnerReceived()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("solo", "# Solo\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        var runner = new CancelAwaitingTaskRunner();
        viewModel.SingleRunTaskRunnerFactory = (_, _) => runner;
        var task = viewModel.Tasks.Single(t => t.Id == "solo");

        var run = viewModel.RunSelectedTaskAsync(task);
        await runner.Started;
        Assert.True(viewModel.CancelRunCommand.CanExecute(null));
        viewModel.CancelRunCommand.Execute(null);
        await run;

        Assert.True(runner.Token.IsCancellationRequested);
        Assert.False(viewModel.CancelRequested);
        Assert.False(viewModel.CancelRunCommand.CanExecute(null));
        Assert.Contains("solo", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains("Cancelled", viewModel.StatusText, StringComparison.Ordinal);
    }

    /// <summary>Holds the run open until its own token is cancelled, then winds down like the driver.</summary>
    private sealed class CancelAwaitingTaskRunner : IRelayTaskRunner
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;
        public CancellationToken Token { get; private set; }

        public async Task<RelayTaskOutcome> RunTaskAsync(
            string rootPath, string taskId, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            _started.SetResult();
            try { await new TaskCompletionSource().Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { /* the driver winds down rather than throwing */ }
            return new RelayTaskOutcome(taskId, RelayTaskOutcomeStatus.Flagged, null, null, "cancelled by operator");
        }
    }
}
