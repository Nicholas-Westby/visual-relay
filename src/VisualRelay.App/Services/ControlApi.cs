using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Threading;
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
        _ => null
    };

    // Property-backed user actions (not ICommands). Names mirror UI affordances.
    private static readonly string[] PropertyActions =
        ["select-task", "bypass-sandbox", "boost-turns", "open-folder"];

    /// <summary>
    /// Invokes a documented command/action by name. Returns the HTTP status and
    /// the JSON body to write. Honors the same enabled gating as the UI: a
    /// disabled command (or a property action whose precondition is unmet) is
    /// refused with 409 and NOT executed. Unknown names → 404.
    /// </summary>
    public Task<(int Status, string Json)> InvokeCommandAsync(string name, string? body) =>
        Dispatcher.UIThread.InvokeAsync(() => InvokeCommandOnUiThread(name, body)).GetTask();

    private (int Status, string Json) InvokeCommandOnUiThread(string name, string? body)
    {
        var command = ResolveCommand(name);
        if (command is not null)
        {
            if (!command.CanExecute(null))
            {
                return (409, Json.Object(("ok", false), ("command", name), ("error", "disabled")));
            }

            // Async (run) commands are fire-and-forget like a button click: we
            // call Execute and return immediately; the operator polls /state.
            command.Execute(null);
            return (200, Json.Object(("ok", true), ("command", name)));
        }

        if (PropertyActions.Contains(name))
        {
            return InvokePropertyAction(name, body);
        }

        return (404, Json.Object(("ok", false), ("error", "unknown command")));
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

            case "bypass-sandbox":
                {
                    var value = Json.ReadBool(body, "value");
                    if (value is null)
                    {
                        return (409, Json.Object(("ok", false), ("command", name), ("error", "missing value")));
                    }

                    viewModel.BypassSandbox = value.Value;
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

            default:
                return (404, Json.Object(("ok", false), ("error", "unknown command")));
        }
    }
}
