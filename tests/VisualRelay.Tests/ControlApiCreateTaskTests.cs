using System.Text.Json;
using Avalonia.Threading;
using VisualRelay.App.Services;
using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

/// <summary>
/// Authoring a task over the control API. A headless operator has no dialog to
/// type into, so <c>create-task</c> has to write the task file, hand back the id
/// and path it chose, and leave the queue holding the new task — all through the
/// same view-model command the GUI's Create button runs.
/// </summary>
[Collection("Headless")]
public sealed class ControlApiCreateTaskTests
{
    private static ControlApi NewApi(TestRepository repo, out MainWindowViewModel viewModel)
    {
        repo.WriteConfig("dotnet test", []);
        viewModel = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        return new ControlApi(viewModel);
    }

    private static string Body(string? title, string? body = null) =>
        JsonSerializer.Serialize(new { title, body });

    [AvaloniaFact]
    public async Task CreateTask_WritesTheNestedMarkdown_AndReturnsTheIdAndPath()
    {
        using var repo = TestRepository.Create();
        var api = NewApi(repo, out _);

        var (status, json) = await api.InvokeCommandAsync(
            "create-task", Body("Add a Widget", "Ship the widget.\n"));

        Assert.Equal(200, status);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("add-a-widget", doc.RootElement.GetProperty("id").GetString());

        var path = doc.RootElement.GetProperty("path").GetString()!;
        Assert.Equal(
            Path.Combine(repo.Root, "llm-tasks", "add-a-widget", "add-a-widget.md"),
            path);
        Assert.Equal("# Add a Widget\n\nShip the widget.\n", await File.ReadAllTextAsync(path));
    }

    [AvaloniaFact]
    public async Task CreateTask_AfterADialogPickedATemplate_ShipsNoAttachments()
    {
        // The dialog's template selection outlives the dialog, and the create
        // command copies the selected template's files into the new folder. A
        // headless caller passed its whole task in the request and asked for no
        // template, so nothing of that session may ride along.
        using var repo = TestRepository.Create();
        var templatesDir = Path.Combine(repo.Root, "llm-tasks", "templates");
        Directory.CreateDirectory(Path.Combine(templatesDir, "kit"));
        await File.WriteAllTextAsync(Path.Combine(templatesDir, "kit.md"),
            "---\nname: Kit\ntitle: Use the kit\n---\nBody\n");
        await File.WriteAllTextAsync(Path.Combine(templatesDir, "kit", "checklist.md"), "step one\n");
        var api = NewApi(repo, out var viewModel);
        viewModel.OpenNewTaskDialogCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var kit = viewModel.NewTaskTemplateNames.IndexOf("Kit");
        Assert.True(kit >= 0, "the Kit template must be in the list");
        viewModel.SelectedNewTaskTemplateIndex = kit;

        var (status, _) = await api.InvokeCommandAsync("create-task", Body("From the api", "Body.\n"));

        Assert.Equal(200, status);
        var taskDirectory = Path.Combine(repo.Root, "llm-tasks", "from-the-api");
        Assert.Equal(
            ["from-the-api.md"],
            Directory.GetFiles(taskDirectory).Select(file => Path.GetFileName(file)!).ToArray());
    }

    [AvaloniaFact]
    public async Task CreateTask_WithoutABody_WritesTitleOnlyMarkdown()
    {
        using var repo = TestRepository.Create();
        var api = NewApi(repo, out _);

        var (status, json) = await api.InvokeCommandAsync("create-task", Body("Only a title"));

        Assert.Equal(200, status);
        using var doc = JsonDocument.Parse(json);
        var path = doc.RootElement.GetProperty("path").GetString()!;
        Assert.Equal("# Only a title\n", await File.ReadAllTextAsync(path));
    }

    [AvaloniaFact]
    public async Task CreateTask_LeavesTheTaskInStateWhenTheCallReturns()
    {
        using var repo = TestRepository.Create();
        var api = NewApi(repo, out _);

        var (status, _) = await api.InvokeCommandAsync("create-task", Body("Queued Work", "Body.\n"));
        Assert.Equal(200, status);

        using var state = JsonDocument.Parse(await api.BuildStateJsonAsync());
        var ids = state.RootElement.GetProperty("tasks")
            .EnumerateArray()
            .Select(t => t.GetProperty("id").GetString())
            .ToArray();
        Assert.Contains("queued-work", ids);
    }

    [AvaloniaFact]
    public async Task CreateTask_WhileTheArchiveIsShowing_SwitchesToTheQueueAndListsIt()
    {
        // The archive lists DONE-* only, so a reload taken from it cannot hold the
        // task just written. Creating a task is a queue action: it switches back to
        // the queue, and only then keeps the promise that the answer names a listed row.
        using var repo = TestRepository.Create();
        var api = NewApi(repo, out var viewModel);
        var (toggled, _) = await api.InvokeCommandAsync("archive-toggle", null);
        Assert.Equal(200, toggled);
        Assert.True(viewModel.ShowArchive);

        var (status, json) = await api.InvokeCommandAsync("create-task", Body("Probe Task", "probe\n"));

        Assert.Equal(200, status);
        Assert.False(viewModel.ShowArchive);
        using var doc = JsonDocument.Parse(json);
        var row = viewModel.Tasks.Single(task => task.Id == "probe-task");
        Assert.Equal(row.Task.MarkdownPath, doc.RootElement.GetProperty("path").GetString());
        Assert.Equal(
            Path.Combine(repo.Root, "llm-tasks", "probe-task", "probe-task.md"),
            row.Task.MarkdownPath);

        using var state = JsonDocument.Parse(await api.BuildStateJsonAsync());
        var ids = state.RootElement.GetProperty("tasks")
            .EnumerateArray()
            .Select(t => t.GetProperty("id").GetString())
            .ToArray();
        Assert.Contains("probe-task", ids);
        Assert.Equal("probe-task", viewModel.SelectedTask?.Id);
    }

    [AvaloniaFact]
    public async Task State_ListsCreateTask_WithAnEnabledFlag()
    {
        using var repo = TestRepository.Create();
        var api = NewApi(repo, out _);

        using var state = JsonDocument.Parse(await api.BuildStateJsonAsync());
        var entry = state.RootElement.GetProperty("commands").GetProperty("create-task");
        Assert.True(entry.GetProperty("enabled").GetBoolean());
    }

    [AvaloniaFact]
    public async Task CreateTask_WithoutATitle_Returns400()
    {
        using var repo = TestRepository.Create();
        var api = NewApi(repo, out _);

        var (status, json) = await api.InvokeCommandAsync("create-task", "{\"body\":\"orphan\"}");

        Assert.Equal(400, status);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("error").GetString()));
        Assert.False(Directory.Exists(Path.Combine(repo.Root, "llm-tasks")));
    }

    [AvaloniaFact]
    public async Task CreateTask_WithATitleThatSlugifiesToNothing_Returns400()
    {
        using var repo = TestRepository.Create();
        var api = NewApi(repo, out _);

        var (status, json) = await api.InvokeCommandAsync("create-task", Body("!!!"));

        Assert.Equal(400, status);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("error").GetString()));
    }

    [AvaloniaFact]
    public async Task CreateTask_WithADuplicateSlug_Returns400_AndKeepsTheFirstTask()
    {
        using var repo = TestRepository.Create();
        var api = NewApi(repo, out _);

        var (first, _) = await api.InvokeCommandAsync("create-task", Body("Same Name", "first\n"));
        Assert.Equal(200, first);

        var (second, json) = await api.InvokeCommandAsync("create-task", Body("Same Name", "second\n"));

        Assert.Equal(400, second);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains(
            "already exists",
            doc.RootElement.GetProperty("error").GetString()!,
            StringComparison.Ordinal);
        Assert.Equal(
            "# Same Name\n\nfirst\n",
            await File.ReadAllTextAsync(Path.Combine(repo.Root, "llm-tasks", "same-name", "same-name.md")));
    }

    [AvaloniaFact]
    public async Task CreateTask_WhileBusy_Returns409_AndWritesNothing()
    {
        using var repo = TestRepository.Create();
        var api = NewApi(repo, out var vm);
        await Dispatcher.UIThread.InvokeAsync(() => vm.IsBusy = true);

        var (status, json) = await api.InvokeCommandAsync("create-task", Body("Busy Task", "body\n"));

        Assert.Equal(409, status);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("disabled", doc.RootElement.GetProperty("error").GetString());
        Assert.False(Directory.Exists(Path.Combine(repo.Root, "llm-tasks", "busy-task")));

        using var state = JsonDocument.Parse(await api.BuildStateJsonAsync());
        Assert.False(state.RootElement.GetProperty("commands")
            .GetProperty("create-task").GetProperty("enabled").GetBoolean());
    }

    [AvaloniaFact]
    public async Task CreateTask_WithNoRootOpen_Returns409()
    {
        using var repo = TestRepository.Create();
        var api = NewApi(repo, out var vm);
        await Dispatcher.UIThread.InvokeAsync(() => vm.RootPath = Path.Combine(repo.Root, "not-a-folder"));

        var (status, json) = await api.InvokeCommandAsync("create-task", Body("No Root", "body\n"));

        Assert.Equal(409, status);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("disabled", doc.RootElement.GetProperty("error").GetString());
    }
}
