using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisualRelay.App.Services;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    // ── Create task to fix ────────────────────────────────────────────────

    /// <summary>
    /// Test seam: builds the sandboxed runner used by "Create task to fix".
    /// Production builds a real <see cref="SwivalSubagentRunner"/>; tests inject a
    /// fake so the fix-task-author path can be exercised without a live subagent.
    /// </summary>
    internal Func<RelayConfig, ISubagentRunner>? FixTaskRunnerFactory { get; set; }

    /// <summary>
    /// True while a "Create task to fix" subagent call is in flight — gates the
    /// button to prevent double-submission.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateFixTaskCommand))]
    [NotifyPropertyChangedFor(nameof(CreateFixTaskButtonLabel))]
    private bool _isCreatingFixTask;

    /// <summary>Label for the fix-task button: changes while working.</summary>
    public string CreateFixTaskButtonLabel => IsCreatingFixTask
        ? "Creating fix task…"
        : "Create task to fix";

    /// <summary>Exposed for tests so they can assert CanExecute state.</summary>
    public bool CanCreateFixTaskPublic => CanCreateFixTask();

    [RelayCommand(CanExecute = nameof(CanCreateFixTask))]
    private async Task CreateFixTaskAsync()
    {
        if (SelectedTask is null)
            return;

        var taskId = SelectedTask.Id;
        var taskDirectory = SelectedTask.Task.TaskDirectory;

        // Confirm via the shared seam.
        var confirmed = await ConfirmAsync(
            "Create task to fix",
            "Author a new task from the failed run's issues? The new task will appear in the queue.",
            "Create");
        if (!confirmed)
            return;

        IsCreatingFixTask = true;
        StatusText = "Creating fix task…";

        var cts = new CancellationTokenSource();
        var ct = cts.Token;

        var config = await RelayConfigLoader.LoadAsync(RootPath, ct);
        var runner = FixTaskRunnerFactory?.Invoke(config)
            ?? new SwivalSubagentRunner(config,
                eventSink: new ObservableRelayEventSink(HandleRelayEvent),
                verboseDiagnostics: VerboseSandboxDiagnostics);

        FixTaskAuthorOutcome outcome;
        try
        {
            outcome = await Task.Run(
                () => FixTaskAuthorRunner.RunAsync(
                    RootPath, taskId, taskDirectory, config, runner, ct),
                ct);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Couldn't create fix task: Cancelled.";
            IsCreatingFixTask = false;
            return;
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't create fix task: {ex.Message}";
            IsCreatingFixTask = false;
            return;
        }

        if (!outcome.Success)
        {
            StatusText = $"Couldn't create fix task: {outcome.Error}";
            IsCreatingFixTask = false;
            return;
        }

        // Sanitize the slug first (the LLM may return unsafe characters).
        var baseSlug = outcome.Slug!;
        if (RelayTaskWriter.ValidateSlug(baseSlug) is not null)
        {
            baseSlug = RelayTaskWriter.Slugify(baseSlug);
            if (string.IsNullOrWhiteSpace(baseSlug))
                baseSlug = "fix-task";
        }

        // Disambiguate slug — append -2, -3, etc. if it collides.
        var finalSlug = baseSlug;
        var suffix = 2;
        while (RelayTaskWriter.ValidateSlug(finalSlug, RootPath) is not null)
        {
            finalSlug = $"{baseSlug}-{suffix}";
            suffix++;
        }

        try
        {
            await RelayTaskWriter.CreateAsync(RootPath, finalSlug, outcome.Markdown!);
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't create fix task: {ex.Message}";
            IsCreatingFixTask = false;
            return;
        }

        // Reload the queue but keep selection on the original task
        // so the completion modal's dismiss path leaves it unchanged.
        await ReloadTaskListAsync(taskId);

        var viewTask = await ConfirmAsync(
            "Fix task created",
            $"Created '{finalSlug}'{(!string.IsNullOrWhiteSpace(outcome.Summary) ? $": {outcome.Summary}" : ".")}",
            "View task");

        if (viewTask)
        {
            SelectedTask = Tasks.FirstOrDefault(t => t.Id == finalSlug);
        }

        IsCreatingFixTask = false;
        StatusText = FormatQueueStatus();
    }

    private bool CanCreateFixTask() =>
        SelectedTask is not null
        && HasSelectedTaskError
        && !IsBusy
        && !IsCreatingFixTask;
}
