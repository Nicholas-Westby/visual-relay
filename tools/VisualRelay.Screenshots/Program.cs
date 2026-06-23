using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using VisualRelay.App;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.Domain;

var output = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine("docs", "images", "visual-relay-main.png"));
var width = args.Length > 1 ? double.Parse(args[1]) : 1440;
var height = args.Length > 2 ? double.Parse(args[2]) : 900;
Directory.CreateDirectory(Path.GetDirectoryName(output)!);

// Redirect XDG_CONFIG_HOME into the screenshot scratch root BEFORE the view-model
// is built. UiStateStore.Load (called in the MainWindowViewModel ctor) otherwise
// reads the developer's real ~/.config/visual-relay/ui-state.json — which made the
// rendered tab/column-width depend on whatever layout that machine last persisted
// (e.g. a collapsed-to-minimum activity column on an empty Output tab). Isolating it
// makes the screenshot deterministic and stops the tool from clobbering real state.
var scratchRoot = Path.GetFullPath(Path.Combine(".relay-scratch", "screenshot-root"));
Directory.CreateDirectory(scratchRoot);
Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(scratchRoot, ".config"));

// The selected task's markdown, held as a literal so SeedActivity can restore it
// post-Show without re-reading the file — selecting the task promotes it to the
// nested layout, which deletes the flat llm-tasks/<id>.md path.
const string demoTaskMarkdown =
    """
    # Add multiply helper

    Implement a typed multiply(a, b) in src/math_tools.py and expose it through the CLI dispatcher so users can run relay calc mul 6 7.

    ## Requirements

    - Mirror the existing add() helper signature and docstring style.
    - Register the verb in COMMANDS and update the --help table.
    - Cover ints, floats and overflow in tests/test_math_tools.py.

    ## Run

    relay run add-multiply
    """;

// ReSharper disable once UseAwaitUsing — one-shot screenshot tool; the headless
// Avalonia session is torn down synchronously at process exit. Sync dispose keeps
// the dispatcher-shutdown ordering simple and avoids async-teardown surprises.
using var session = HeadlessUnitTestSession.StartNew(typeof(ScreenshotAppBuilder));
await session.Dispatch(async () =>
{
    var viewModel = BuildViewModel(scratchRoot, demoTaskMarkdown);
    var window = new MainWindow
    {
        DataContext = viewModel,
        Width = width,
        Height = height
    };
    window.Show();
    await Task.Delay(100);
    SeedActivity(viewModel, demoTaskMarkdown);
    var frame = window.CaptureRenderedFrame();
    if (frame is null)
    {
        throw new InvalidOperationException("Avalonia did not render a frame.");
    }

    SaveBitmap(frame, output);
    return true;
}, CancellationToken.None);

Console.WriteLine(output);
Environment.Exit(0);

static MainWindowViewModel BuildViewModel(string root, string demoTaskMarkdown)
{
    Directory.CreateDirectory(Path.Combine(root, ".relay"));
    Directory.CreateDirectory(Path.Combine(root, "llm-tasks"));
    File.WriteAllText(
        Path.Combine(root, ".relay", "config.json"),
        """
        {
          "testCmd": "dotnet test",
          "logSources": ["logs/app.log"]
        }
        """);
    var taskPath = WriteTask(root, "add-multiply-helper", demoTaskMarkdown);

    var viewModel = new MainWindowViewModel
    {
        RootPath = root,
        StatusText = "Pause armed: finishing add-multiply-helper before stopping",
        SelectedTaskMetricLabel = "11 stages  2m 18s  $0.07",
        LogScopeLabel = "full",
        IsBusy = true,
        PauseRequested = true,
        // Pin the activity panel to the populated Run Log tab at a comfortable
        // width so the screenshot never inherits a stale/collapsed layout.
        ActivityTabIndex = 0,
        ActivityColumnWidth = 360
    };
    var task = new TaskRowViewModel(
        new RelayTaskItem("add-multiply-helper", taskPath, Path.GetDirectoryName(taskPath)!, false, [], CostUsd: 0.0731, DurationSeconds: 138, CompletedStageCount: 11));
    viewModel.Tasks.Add(task);
    viewModel.Tasks.Add(DemoTask(root, "fix-csv-export-encoding", costUsd: 0.0018, seconds: 64, stages: 11));
    viewModel.Tasks.Add(DemoTask(root, "rate-limit-middleware", costUsd: 0.0121, seconds: 284, stages: 11));
    viewModel.Tasks.Add(DemoTask(root, "stabilise-flaky-retry-test", costUsd: 0.0032, seconds: 95, stages: 11));
    viewModel.Tasks.Add(DemoTask(root, "extract-theme-tokens", "swival exit 2", costUsd: 0.0009, seconds: 31, stages: 2));
    viewModel.RestoreRunningTaskState(task.Id, 3, "Diagnose");
    // Setting SelectedTask kicks off an async load that resets the stage board and
    // reads markdown/context/run-history from the (empty) scratch project. The
    // dynamic display state is therefore applied AFTER Show()+settle in SeedActivity,
    // so it wins over that load instead of racing it.
    viewModel.SelectedTask = task;
    return viewModel;
}

// Seeds the stage board so its cards exercise the current status-row + metrics
// layout: a Done card reads "Completed in 19s" with a "cost  turns  test" metric
// line, and the active card ticks "Running {elapsed}".
static void SeedStages(MainWindowViewModel viewModel)
{
    ApplyDoneStage(viewModel.Stages[0], "19s", "$0.00", "6t", 3);   // Ideate
    ApplyDoneStage(viewModel.Stages[1], "22s", "$0.01", "11t", 4);  // Research
    viewModel.Stages[1].IsSelected = true;

    // Mark Diagnose running ~42s ago so the status row shows the live "Running 42s"
    // tick (StatusLabel reads MarkRunning's elapsed, not the DurationLabel).
    var diagnose = viewModel.Stages[2];
    diagnose.MarkRunning(DateTimeOffset.UtcNow.AddSeconds(-42));
    diagnose.RefreshElapsed(DateTimeOffset.UtcNow);
    diagnose.CostLabel = "$0.01";
    diagnose.TurnsLabel = "7t";
}

static void ApplyDoneStage(StageRowViewModel stage, string duration, string cost, string turns, double testSeconds)
{
    stage.DurationLabel = duration;
    stage.CostLabel = cost;
    stage.TurnsLabel = turns;
    stage.SetTestDurationSeconds(testSeconds);
    stage.Status = "Done";
}

static string WriteTask(string root, string id, string markdown)
{
    var path = Path.Combine(root, "llm-tasks", $"{id}.md");
    File.WriteAllText(path, markdown);
    return path;
}

static TaskRowViewModel DemoTask(string root, string id, string? reviewReason = null, double costUsd = 0, double seconds = 0, int stages = 0)
{
    var path = WriteTask(root, id, $"# {id}\n\nDemo task used to exercise the Visual Relay control room.");
    return new TaskRowViewModel(new RelayTaskItem(id, path, Path.GetDirectoryName(path)!, false, [], reviewReason, CostUsd: costUsd, DurationSeconds: seconds, CompletedStageCount: stages));
}

// Applies the dynamic display state AFTER the window is shown and the SelectedTask
// load has settled, so it wins over that load: the selected-task metric chip, the
// markdown/context detail, the stage board, and the Activity column's default Run
// Log tab (stage lifecycle events with model/time/cost data plus the Commands-tab
// trace entries) — so the right-hand panel reads as active, not empty.
static void SeedActivity(MainWindowViewModel viewModel, string demoTaskMarkdown)
{
    var now = DateTimeOffset.UtcNow;
    var root = viewModel.RootPath;
    var taskId = viewModel.SelectedTask?.Id ?? "add-multiply-helper";

    viewModel.SelectedTaskMetricLabel = "11 stages  2m 18s  $0.07";
    viewModel.SelectedTaskMarkdown = demoTaskMarkdown;
    viewModel.SelectedTaskContext = "### logs/app.log\n12:04:41 [plan] 3 edits planned across 3 files\n13:08:54 [implement] stage complete in 28s";
    SeedStages(viewModel);

    viewModel.Events.Clear();
    viewModel.Events.Add(new RelayEvent(now.AddSeconds(-2), "info", "stage_start", "demo", root, taskId, 3, "balanced",
        Data: new Dictionary<string, string> { ["name"] = "Diagnose", ["model"] = "balanced" }));
    viewModel.Events.Add(new RelayEvent(now.AddSeconds(-7), "info", "trace", "demo", root, taskId, 3, "balanced",
        Data: new Dictionary<string, string> { ["title"] = "read_file", ["time"] = "1s", ["cost"] = "$0.00" }));
    viewModel.Events.Add(new RelayEvent(now.AddSeconds(-22), "info", "stage_done", "demo", root, taskId, 2, "cheap",
        Data: new Dictionary<string, string> { ["name"] = "Research", ["model"] = "cheap", ["time"] = "22s", ["cost"] = "$0.01" }));
    viewModel.Events.Add(new RelayEvent(now.AddSeconds(-24), "info", "stage_report", "demo", root, taskId, 2, "cheap",
        Data: new Dictionary<string, string> { ["name"] = "Research", ["model"] = "cheap", ["time"] = "22s", ["cost"] = "$0.01" }));
    viewModel.Events.Add(new RelayEvent(now.AddSeconds(-30), "warn", "tests_red", "demo", root, taskId, 2, "cheap",
        Data: new Dictionary<string, string> { ["reason"] = "2 failing before implementation", ["time"] = "4s" }));
    viewModel.Events.Add(new RelayEvent(now.AddSeconds(-45), "info", "stage_done", "demo", root, taskId, 1, "cheap",
        Data: new Dictionary<string, string> { ["name"] = "Ideate", ["model"] = "cheap", ["time"] = "19s", ["cost"] = "$0.00" }));

    viewModel.TraceEntries.Clear();
    viewModel.TraceEntries.Add(new TraceEntry(TraceEntryKind.ToolCall, "Research", "rg \"COMMANDS|add\" src tests", 2));
    viewModel.TraceEntries.Add(new TraceEntry(TraceEntryKind.AssistantText, "Findings", "Existing CLI dispatcher omits the mul verb; tests cover add/sub/div only.", 2));
}

static void SaveBitmap(Bitmap bitmap, string path)
{
    using var stream = File.Create(path);
    bitmap.Save(stream);
}

public static class ScreenshotAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false
            })
            .WithInterFont();
}
