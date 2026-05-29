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
Directory.CreateDirectory(Path.GetDirectoryName(output)!);

using var session = HeadlessUnitTestSession.StartNew(typeof(ScreenshotAppBuilder));
await session.Dispatch(async () =>
{
    var viewModel = BuildViewModel();
    var window = new MainWindow
    {
        DataContext = viewModel,
        Width = 1280,
        Height = 820
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
    var taskPath = Path.Combine(root, "llm-tasks", "repair-login-flow.md");
    File.WriteAllText(taskPath, "# Repair login flow\n\nReproduce the redirect issue, add a red test, implement the fix, and verify the suite.");

    var viewModel = new MainWindowViewModel
    {
        RootPath = root,
        StatusText = "1 pending"
    };
    var task = new RelayTaskItem("repair-login-flow", taskPath, Path.GetDirectoryName(taskPath)!, false, []);
    viewModel.Tasks.Add(task);
    viewModel.SelectedTask = task;
    viewModel.SelectedTaskMarkdown = File.ReadAllText(taskPath);
    viewModel.SelectedTaskContext = "### logs/app.log\nPOST /session returned 302 without the expected returnUrl.";

    viewModel.Stages[0].Status = "Done";
    viewModel.Stages[1].Status = "Done";
    viewModel.Stages[2].Status = "Running";
    viewModel.Events.Add(new RelayEvent(DateTimeOffset.UtcNow, "info", "stage_start", "demo", root, task.Id, 3, "balanced"));
    viewModel.Events.Add(new RelayEvent(DateTimeOffset.UtcNow.AddSeconds(-12), "info", "stage_done", "demo", root, task.Id, 2, "cheap"));
    return viewModel;
}

static void SeedTraceEntries(MainWindowViewModel viewModel)
{
    viewModel.TraceEntries.Clear();
    viewModel.TraceEntries.Add(new TraceEntry(TraceEntryKind.AssistantText, "text", "I am checking the login handler and the failing redirect test."));
    viewModel.TraceEntries.Add(new TraceEntry(TraceEntryKind.ToolCall, "shell", "{\"cmd\":\"dotnet test --filter Login\"}"));
    viewModel.TraceEntries.Add(new TraceEntry(TraceEntryKind.ToolResult, "tool_result", "Failed: LoginRedirectTests.PreservesReturnUrl"));
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
