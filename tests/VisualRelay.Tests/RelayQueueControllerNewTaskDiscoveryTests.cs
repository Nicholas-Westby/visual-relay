using VisualRelay.Core.Queue;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for recognizing new tasks created during a "Run All" drain.
/// </summary>
public sealed class RelayQueueControllerNewTaskDiscoveryTests
{
    [Fact]
    public async Task DrainAsync_TaskAddedMidRun_IsPickedUpAfterBatch()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");
        var runner = new RecordingTaskRunner();
        var controller = new RelayQueueController(repo.Root, runner);
        // ReSharper disable once AccessToDisposedClosure — fires during DrainAsync; repo alive until method exit.
        runner.AfterRun = () =>
        {
            if (runner.TasksRun.Count == 1)
            {
                var deltaPath = Path.Combine(repo.Root, "llm-tasks", "delta.md");
                controller.Tasks.Add(new RelayTaskItem("delta", deltaPath,
                    Path.GetDirectoryName(deltaPath)!, false, []));
            }
        };

        await controller.RefreshAsync();
        await controller.DrainAsync();

        // Delta was added mid-drain but is picked up after the original batch.
        Assert.Equal(["alpha", "beta", "gamma", "delta"], runner.TasksRun);
        Assert.Equal(RelayQueueState.Completed, controller.State);
        // Delta was committed and removed.
        Assert.DoesNotContain(controller.Tasks, t => t.Id == "delta");
    }

    [Fact]
    public async Task DrainAsync_InterleavedRefreshDoesNotChangeOrderOrDuplicate()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");
        var runner = new RecordingTaskRunner();
        var controller = new RelayQueueController(repo.Root, runner);
        // ReSharper disable once AccessToDisposedClosure — fires during DrainAsync; repo alive until method exit.
        runner.AfterRunAsync = async () =>
        {
            if (runner.TasksRun.Count == 1)
            {
                // New task on disk mid-run; refresh re-sorts so aardvark shifts before beta.
                repo.WriteTask("aardvark", "# Aardvark\n");
                await controller.RefreshAsync();
            }
        };

        await controller.RefreshAsync();
        await controller.DrainAsync();

        // Original batch runs first, then aardvark is picked up as a new task.
        Assert.Equal(["alpha", "beta", "gamma", "aardvark"], runner.TasksRun);
        Assert.Equal(RelayQueueState.Completed, controller.State);
        Assert.DoesNotContain(controller.Tasks, t => t.Id == "aardvark");
    }

    [Fact]
    public async Task DrainAsync_Sequential_PicksUpNewTaskBetweenTasks()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");
        var runner = new RecordingTaskRunner();
        var controller = new RelayQueueController(repo.Root, runner);
        // ReSharper disable once AccessToDisposedClosure — fires during DrainAsync; repo alive until method exit.
        runner.AfterRun = () =>
        {
            if (runner.TasksRun.Count == 1)
            {
                var deltaPath = Path.Combine(repo.Root, "llm-tasks", "delta.md");
                controller.Tasks.Add(new RelayTaskItem("delta", deltaPath,
                    Path.GetDirectoryName(deltaPath)!, false, []));
            }
        };

        await controller.RefreshAsync();
        await controller.DrainAsync(mode: RunAllMode.Sequential);

        Assert.Equal(["alpha", "beta", "gamma", "delta"], runner.TasksRun);
        Assert.Equal(RelayQueueState.Completed, controller.State);
        Assert.DoesNotContain(controller.Tasks, t => t.Id == "delta");
    }

    [Fact]
    public async Task DrainAsync_Sequential_NewTaskReordered_ProcessedInNewPosition()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");
        var runner = new RecordingTaskRunner();
        var controller = new RelayQueueController(repo.Root, runner);
        // ReSharper disable once AccessToDisposedClosure — fires during DrainAsync; repo alive until method exit.
        runner.AfterRun = () =>
        {
            if (runner.TasksRun.Count == 1)
            {
                var deltaPath = Path.Combine(repo.Root, "llm-tasks", "delta.md");
                controller.Tasks.Add(new RelayTaskItem("delta", deltaPath,
                    Path.GetDirectoryName(deltaPath)!, false, []));
                // Move delta up once: [alpha,beta,gamma,delta] → [alpha,beta,delta,gamma]
                controller.MoveUp("delta");
            }
        };

        await controller.RefreshAsync();
        await controller.DrainAsync(mode: RunAllMode.Sequential);

        // Delta was reordered before gamma → runs before gamma.
        Assert.Equal(["alpha", "beta", "delta", "gamma"], runner.TasksRun);
        Assert.Equal(RelayQueueState.Completed, controller.State);
    }

    [Fact]
    public async Task DrainAsync_Standard_NewTaskWaitsForFullBatch()
    {
        // Even in Standard mode (the default), new tasks are picked up — but only
        // after the entire initial batch finishes, not at each task boundary.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");
        var runner = new RecordingTaskRunner();
        var controller = new RelayQueueController(repo.Root, runner);
        // ReSharper disable once AccessToDisposedClosure — fires during DrainAsync; repo alive until method exit.
        runner.AfterRun = () =>
        {
            if (runner.TasksRun.Count == 1)
            {
                var deltaPath = Path.Combine(repo.Root, "llm-tasks", "delta.md");
                controller.Tasks.Add(new RelayTaskItem("delta", deltaPath,
                    Path.GetDirectoryName(deltaPath)!, false, []));
            }
        };

        await controller.RefreshAsync();
        await controller.DrainAsync(mode: RunAllMode.Standard);

        // Delta runs last, after the original three finish.
        Assert.Equal(["alpha", "beta", "gamma", "delta"], runner.TasksRun);
        Assert.Equal(RelayQueueState.Completed, controller.State);
        Assert.DoesNotContain(controller.Tasks, t => t.Id == "delta");
    }

    [Fact]
    public async Task DrainAsync_NewTaskAfterHalt_IsNotRun()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");
        var added = false;
        RelayQueueController? capturedController = null;
        // ReSharper disable once AccessToDisposedClosure — fires during DrainAsync; repo alive until method exit.
        var injector = new InjectingTaskRunner(new CommitRejectingTaskRunner(), () =>
        {
            if (!added && capturedController is not null)
            {
                added = true;
                var deltaPath = Path.Combine(repo.Root, "llm-tasks", "delta.md");
                capturedController.Tasks.Add(new RelayTaskItem("delta", deltaPath,
                    Path.GetDirectoryName(deltaPath)!, false, []));
            }
        });
        var controller = new RelayQueueController(repo.Root, injector);
        capturedController = controller;

        await controller.RefreshAsync();
        var results = await controller.DrainAsync();

        // Alpha + beta flagged → halt. Gamma and delta never start.
        Assert.Equal(["alpha", "beta"], results.Select(r => r.TaskId));
        Assert.Equal(RelayQueueState.Failed, controller.State);
        Assert.Contains(controller.Tasks, t => t is { Id: "gamma", NeedsReview: false });
        Assert.Contains(controller.Tasks, t => t is { Id: "delta", NeedsReview: false });
    }

    [Fact]
    public async Task DrainAsync_Sequential_MultipleNewTasks_MergedInOrder()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");
        var runner = new RecordingTaskRunner();
        var controller = new RelayQueueController(repo.Root, runner);
        // ReSharper disable once AccessToDisposedClosure — fires during DrainAsync; repo alive until method exit.
        runner.AfterRun = () =>
        {
            if (runner.TasksRun.Count == 1)
            {
                var epsilonPath = Path.Combine(repo.Root, "llm-tasks", "epsilon.md");
                controller.Tasks.Add(new RelayTaskItem("epsilon", epsilonPath,
                    Path.GetDirectoryName(epsilonPath)!, false, []));
                var zetaPath = Path.Combine(repo.Root, "llm-tasks", "zeta.md");
                controller.Tasks.Add(new RelayTaskItem("zeta", zetaPath,
                    Path.GetDirectoryName(zetaPath)!, false, []));
                // Move zeta before epsilon → [..., epsilon, zeta] → [..., zeta, epsilon]
                controller.MoveUp("zeta");
            }
        };

        await controller.RefreshAsync();
        await controller.DrainAsync(mode: RunAllMode.Sequential);

        Assert.Equal(["alpha", "beta", "gamma", "zeta", "epsilon"], runner.TasksRun);
        Assert.Equal(RelayQueueState.Completed, controller.State);
    }

    [Fact]
    public async Task DrainAsync_PauseWithNewTasks_WaitsForNextDrain()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("beta", "# Beta\n");
        repo.WriteTask("gamma", "# Gamma\n");
        var runner = new RecordingTaskRunner();
        var controller = new RelayQueueController(repo.Root, runner);
        // ReSharper disable once AccessToDisposedClosure — fires during DrainAsync; repo alive until method exit.
        runner.AfterRun = () =>
        {
            if (runner.TasksRun.Count == 1)
            {
                controller.RequestPause();
                var deltaPath = Path.Combine(repo.Root, "llm-tasks", "delta.md");
                controller.Tasks.Add(new RelayTaskItem("delta", deltaPath,
                    Path.GetDirectoryName(deltaPath)!, false, []));
            }
        };

        await controller.RefreshAsync();
        var results = await controller.DrainAsync(mode: RunAllMode.Sequential);

        // Only alpha ran before pause; delta never started.
        Assert.Equal(["alpha"], runner.TasksRun);
        Assert.Equal(RelayQueueState.Paused, controller.State);
        // Delta remains for the next drain.
        Assert.Contains(controller.Tasks, t => t is { Id: "delta", NeedsReview: false });
    }
}
