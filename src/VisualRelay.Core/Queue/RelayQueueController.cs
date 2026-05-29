using System.Collections.ObjectModel;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.Core.Queue;

public sealed class RelayQueueController
{
    private readonly IRelayTaskRunner _runner;
    private readonly RelayTaskRepository _repository;
    private bool _pauseRequested;

    public RelayQueueController(string rootPath, IRelayTaskRunner runner)
    {
        RootPath = rootPath;
        _runner = runner;
        _repository = new RelayTaskRepository(rootPath);
    }

    public string RootPath { get; }
    public ObservableCollection<RelayTaskItem> Tasks { get; } = [];
    public RelayQueueState State { get; private set; } = RelayQueueState.Idle;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        State = RelayQueueState.Refreshing;
        Tasks.Clear();
        foreach (var task in await _repository.ListPendingAsync(cancellationToken))
        {
            Tasks.Add(task);
        }

        State = RelayQueueState.Idle;
    }

    public void RequestPause()
    {
        _pauseRequested = true;
        if (State == RelayQueueState.Running)
        {
            State = RelayQueueState.PauseRequested;
        }
    }

    public void MoveUp(string taskId)
    {
        var index = IndexOf(taskId);
        if (index > 0)
        {
            Tasks.Move(index, index - 1);
        }
    }

    public void MoveDown(string taskId)
    {
        var index = IndexOf(taskId);
        if (index >= 0 && index < Tasks.Count - 1)
        {
            Tasks.Move(index, index + 1);
        }
    }

    public async Task<IReadOnlyList<RelayTaskOutcome>> DrainAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<RelayTaskOutcome>();
        _pauseRequested = false;
        State = RelayQueueState.Running;

        while (Tasks.Count > 0)
        {
            var task = Tasks[0];
            var outcome = await _runner.RunTaskAsync(RootPath, task.Id, cancellationToken);
            results.Add(outcome);
            Tasks.RemoveAt(0);

            if (_pauseRequested)
            {
                State = RelayQueueState.Paused;
                return results;
            }
        }

        State = RelayQueueState.Completed;
        return results;
    }

    private int IndexOf(string taskId)
    {
        for (var i = 0; i < Tasks.Count; i++)
        {
            if (string.Equals(Tasks[i].Id, taskId, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}

