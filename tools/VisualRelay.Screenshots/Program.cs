using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Skia;
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

using var session = HeadlessUnitTestSession.StartNew(typeof(ScreenshotAppBuilder));
await session.Dispatch(async () =>
{
    var viewModel = BuildViewModel();
    var window = new MainWindow
    {
        DataContext = viewModel,
        Width = width,
        Height = height
    };
    window.Show();
    await Task.Delay(100);
    SeedTraceEntries(viewModel);
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

static MainWindowViewModel BuildViewModel()
{
    var root = Path.GetFullPath(Path.Combine(".relay-scratch", "screenshot-root"));
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
    var taskPath = WriteTask(
        root,
        "add-multiply-helper",
        """
        # Add multiply helper

        Implement a typed multiply(a, b) in src/math_tools.py and expose it through the CLI dispatcher so users can run relay calc mul 6 7.

        ## Requirements

        - Mirror the existing add() helper signature and docstring style.
        - Register the verb in COMMANDS and update the --help table.
        - Cover ints, floats and overflow in tests/test_math_tools.py.

        ## Run

        relay run add-multiply
        """);

    var viewModel = new MainWindowViewModel
    {
        RootPath = root,
        StatusText = "Pause armed: finishing add-multiply-helper before stopping",
        SelectedTaskMetricLabel = "6 steps  2m 18s  $0.01",
        LogScopeLabel = "stage 02",
        IsBusy = true,
        PauseRequested = true
    };
    var task = new TaskRowViewModel(
        new RelayTaskItem("add-multiply-helper", taskPath, Path.GetDirectoryName(taskPath)!, false, [], CostUsd: 0.0064, DurationSeconds: 138, CompletedStageCount: 6));
    viewModel.Tasks.Add(task);
    viewModel.Tasks.Add(DemoTask(root, "fix-csv-export-encoding", costUsd: 0.0018, seconds: 64, stages: 3));
    viewModel.Tasks.Add(DemoTask(root, "rate-limit-middleware", costUsd: 0.0121, seconds: 284, stages: 11));
    viewModel.Tasks.Add(DemoTask(root, "stabilise-flaky-retry-test", costUsd: 0.0032, seconds: 95, stages: 4));
    viewModel.Tasks.Add(DemoTask(root, "extract-theme-tokens", "swival exit 2", costUsd: 0.0009, seconds: 31, stages: 2));
    viewModel.RestoreRunningTaskState(task.Id, 3, "Diagnose");
    viewModel.SelectedTask = task;
    viewModel.SelectedTaskMarkdown = File.ReadAllText(taskPath);
    viewModel.SelectedTaskContext = "### logs/app.log\n12:04:41 [plan] 3 edits planned across 3 files\n13:08:54 [implement] stage complete in 28s";

    viewModel.Stages[0].Status = "Done";
    viewModel.Stages[1].Status = "Done";
    viewModel.Stages[2].Status = "Running";
    viewModel.Stages[0].DurationLabel = "19s";
    viewModel.Stages[0].CostLabel = "$0.00";
    viewModel.Stages[1].DurationLabel = "22s";
    viewModel.Stages[1].CostLabel = "$0.00";
    viewModel.Stages[2].DurationLabel = "42s";
    viewModel.Stages[2].CostLabel = "$0.00";
    viewModel.Stages[1].IsSelected = true;
    viewModel.Events.Add(new RelayEvent(DateTimeOffset.UtcNow, "info", "stage_report", "demo", root, task.Id, 2, "cheap",
        Data: new Dictionary<string, string> { ["name"] = "Research", ["model"] = "cheap-kimi", ["time"] = "22s", ["cost"] = "$0.00" }));
    viewModel.Events.Add(new RelayEvent(DateTimeOffset.UtcNow.AddSeconds(-12), "info", "stage_done", "demo", root, task.Id, 2, "cheap",
        Data: new Dictionary<string, string> { ["name"] = "Research", ["model"] = "cheap-kimi", ["time"] = "22s", ["cost"] = "$0.00" }));
    viewModel.Events.Add(new RelayEvent(DateTimeOffset.UtcNow.AddSeconds(-28), "info", "tests_written", "demo", root, task.Id, 5, "balanced"));
    viewModel.Events.Add(new RelayEvent(DateTimeOffset.UtcNow.AddSeconds(-36), "info", "plan_accepted", "demo", root, task.Id, 4, "balanced"));
    return viewModel;
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

static void SeedTraceEntries(MainWindowViewModel viewModel)
{
    viewModel.SelectedTaskMetricLabel = "6 steps  2m 18s  $0.01";
    viewModel.LogScopeLabel = "stage 02";
    foreach (var stage in viewModel.Stages)
    {
        stage.IsSelected = false;
    }

    viewModel.Stages[0].Status = "Done";
    viewModel.Stages[0].DurationLabel = "19s";
    viewModel.Stages[0].CostLabel = "$0.00";
    viewModel.Stages[1].Status = "Done";
    viewModel.Stages[1].DurationLabel = "22s";
    viewModel.Stages[1].CostLabel = "$0.00";
    viewModel.Stages[2].Status = "Running";
    viewModel.Stages[2].DurationLabel = "42s";
    viewModel.Stages[2].CostLabel = "$0.00";
    viewModel.Stages[1].IsSelected = true;

    var root = viewModel.RootPath;
    var taskId = viewModel.SelectedTask?.Id ?? "add-multiply-helper";
    viewModel.Events.Clear();
    viewModel.Events.Add(new RelayEvent(DateTimeOffset.UtcNow, "info", "stage_report", "demo", root, taskId, 2, "cheap",
        Data: new Dictionary<string, string> { ["name"] = "Research", ["model"] = "cheap-kimi", ["time"] = "22s", ["cost"] = "$0.00" }));
    viewModel.Events.Add(new RelayEvent(DateTimeOffset.UtcNow.AddSeconds(-12), "info", "trace", "demo", root, taskId, 2, "cheap",
        Data: new Dictionary<string, string> { ["title"] = "read_file", ["time"] = "1s", ["cost"] = "$0.00" }));

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
