using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using VisualRelay.App.ViewModels;

namespace VisualRelay.App.Services;

/// <summary>
/// Unit-testable core behind the localhost HTTP control surface
/// (<see cref="ControlServer"/>). Holds the live <see cref="MainWindowViewModel"/>
/// and <see cref="Window"/> and performs ALL access to them on the Avalonia UI
/// thread via <see cref="Dispatcher.UIThread"/>. The HttpListener callback runs
/// on a background thread, so every method here marshals onto the UI thread
/// before touching VM/window state.
///
/// The API exposes ONLY actions a user can take through the UI, and honors the
/// same enabled/disabled gating: command invocations consult each
/// <see cref="ICommand.CanExecute(object)"/> exactly as the bound button would,
/// refusing (HTTP 409) when the command is disabled rather than executing.
/// </summary>
public sealed partial class ControlApi(MainWindowViewModel viewModel, Window window)
{
    /// <summary>
    /// Resolves the <see cref="ICommand"/> for a documented command name, or
    /// null when the name is not an ICommand-backed action. Must be called on
    /// the UI thread (it reads generated command properties).
    /// </summary>
    private ICommand? ResolveCommand(string name) => name switch
    {
        "bootstrap" => viewModel.BootstrapProjectCommand,
        "run-all" => viewModel.DrainQueueCommand,
        "run-selected" => viewModel.RunSelectedCommand,
        "resume" => viewModel.ResumeSelectedCommand,
        "refresh" => viewModel.RefreshCommand,
        "pause-toggle" => viewModel.TogglePauseCommand,
        "archive-toggle" => viewModel.ToggleArchiveCommand,
        "new-task" => viewModel.OpenNewTaskDialogCommand,
        "follow-running" => viewModel.FollowRunningTaskCommand,
        "start-backend" => viewModel.StartBackendCommand,
        "edit" => viewModel.EditSelectedTaskCommand,
        "rewrite-selected" => viewModel.RewriteSelectedTaskCommand,
        "cancel-rewrite" => viewModel.CancelRewriteSelectedCommand,
        "revert-rewrite" => viewModel.RevertRewriteSelectedCommand,
        "mark-done" => viewModel.MarkSelectedTaskDoneCommand,
        _ => null
    };

    // Property-backed user actions (not ICommands). Names mirror UI affordances.
    private static readonly string[] PropertyActions =
        ["select-task", "boost-turns", "open-folder", "obsidian-scan", "obsidian-bridge",
         "select-activity-tab", "select-detail-tab"];

    // Destructive, confirm-gated commands. Each awaits the VM's confirmation seam
    // (a modal in the GUI). Driven via the API they are PRE-CONFIRMED — they run
    // without opening the modal — but require an explicit {"confirm":true} body,
    // mirroring the human clicking "confirm". Keyed on command identity.
    private static readonly HashSet<string> ConfirmGatedCommands =
        new(StringComparer.Ordinal) { "mark-done", "rewrite-selected" };

    /// <summary>
    /// Invokes a documented command/action by name. Returns the HTTP status and
    /// the JSON body to write. Honors the same enabled gating as the UI: a
    /// disabled command (or a property action whose precondition is unmet) is
    /// refused with 409 and NOT executed. Unknown names → 404.
    /// </summary>
    public Task<(int Status, string Json)> InvokeCommandAsync(string name, string? body) =>
        // The Func<Task<T>> overload of InvokeAsync awaits the inner task for us,
        // so the returned Task completes only once the command has fully run.
        Dispatcher.UIThread.InvokeAsync(() => InvokeCommandOnUiThreadAsync(name, body));

    private async Task<(int Status, string Json)> InvokeCommandOnUiThreadAsync(string name, string? body)
    {
        var command = ResolveCommand(name);
        if (command is not null)
        {
            if (!command.CanExecute(null))
            {
                return (409, Json.Object(("ok", false), ("command", name), ("error", "disabled")));
            }

            if (ConfirmGatedCommands.Contains(name))
            {
                return await InvokeConfirmGatedAsync(name, command, body);
            }

            // Non-confirm commands keep button-click semantics: fire and return
            // immediately; long-running run commands are polled via /state.
            command.Execute(null);
            return (200, Json.Object(("ok", true), ("command", name)));
        }

        if (PropertyActions.Contains(name))
        {
            return InvokePropertyAction(name, body);
        }

        return (404, Json.Object(("ok", false), ("error", "unknown command")));
    }

    /// <summary>
    /// Runs a destructive, confirm-gated command via the pre-confirmed path: it
    /// requires an explicit {"confirm":true} body (else 409 + no-op), then runs
    /// inside the VM's auto-confirm scope so it completes WITHOUT the modal, and
    /// AWAITS it to real completion — so {ok:true} means the effect took (e.g.
    /// mark-done actually archived and the task left the queue, visible in /state).
    /// </summary>
    private async Task<(int Status, string Json)> InvokeConfirmGatedAsync(string name, ICommand command, string? body)
    {
        if (Json.ReadBool(body, "confirm") != true)
        {
            return (409, Json.Object(("ok", false), ("command", name), ("error", "confirmation required")));
        }

        await viewModel.InvokePreConfirmedAsync(async () =>
        {
            if (command is IAsyncRelayCommand asyncCommand)
            {
                await asyncCommand.ExecuteAsync(null);
            }
            else
            {
                command.Execute(null);
            }
        });

        return (200, Json.Object(("ok", true), ("command", name)));
    }

    private (int Status, string Json) InvokePropertyAction(string name, string? body)
    {
        switch (name)
        {
            case "select-task":
                {
                    if (viewModel.Tasks.Count == 0)
                    {
                        return (409, Json.Object(("ok", false), ("command", name), ("error", "disabled")));
                    }

                    var id = Json.ReadString(body, "id");
                    if (string.IsNullOrEmpty(id))
                    {
                        return (409, Json.Object(("ok", false), ("command", name), ("error", "task not found")));
                    }

                    var match = viewModel.Tasks.FirstOrDefault(t => t.Id == id);
                    if (match is null)
                    {
                        return (409, Json.Object(("ok", false), ("command", name), ("error", "task not found")));
                    }

                    viewModel.SelectedTask = match;
                    return (200, Json.Object(("ok", true), ("command", name)));
                }

            case "boost-turns":
                {
                    // Only meaningful with a selected task (mirrors CanToggleTurnBudget's
                    // selected-task precondition); refuse like a disabled control.
                    if (viewModel.SelectedTask is null)
                    {
                        return (409, Json.Object(("ok", false), ("command", name), ("error", "disabled")));
                    }

                    var value = Json.ReadBool(body, "value");
                    if (value is null)
                    {
                        return (409, Json.Object(("ok", false), ("command", name), ("error", "missing value")));
                    }

                    viewModel.SelectedTaskBoostsTurns = value.Value;
                    return (200, Json.Object(("ok", true), ("command", name)));
                }

            case "open-folder":
                {
                    // Programmatic equivalent of the Browse button: point the app at a
                    // project folder. Like Browse it is always available; mirrors
                    // BrowseAsync (set RootPath, then refresh the task list).
                    var path = Json.ReadString(body, "path");
                    if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                    {
                        return (409, Json.Object(("ok", false), ("command", name), ("error", "folder not found")));
                    }

                    viewModel.RootPath = path;
                    if (viewModel.RefreshCommand.CanExecute(null))
                    {
                        viewModel.RefreshCommand.Execute(null);
                    }

                    return (200, Json.Object(("ok", true), ("command", name)));
                }

            case "obsidian-scan":
                {
                    _ = viewModel.RunObsidianBridgeScanAsync();
                    return (200, Json.Object(("ok", true), ("command", name)));
                }

            case "obsidian-bridge":
                {
                    // Toggle enabled: {"value": true|false}
                    var boolVal = Json.ReadBool(body, "value");
                    if (boolVal.HasValue)
                    {
                        viewModel.ObsidianEnabled = boolVal.Value;
                        return (200, Json.Object(("ok", true), ("command", name)));
                    }

                    // Set vault path: {"path": "…"}
                    var path = Json.ReadString(body, "path");
                    if (path is not null)
                    {
                        viewModel.ObsidianVaultRoot = path;
                        return (200, Json.Object(("ok", true), ("command", name)));
                    }

                    return (409, Json.Object(("ok", false), ("command", name), ("error", "missing value or path")));
                }

            case "select-activity-tab":
                return SelectActivityTab(name, body);

            case "select-detail-tab":
                return SelectDetailTab(name, body);

            default:
                return (404, Json.Object(("ok", false), ("error", "unknown command")));
        }
    }
}
