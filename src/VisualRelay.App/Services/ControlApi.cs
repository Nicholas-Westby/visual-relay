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
public sealed partial class ControlApi(
    MainWindowViewModel viewModel,
    Window window,
    IReadOnlyCollection<string>? confirmGatedCommands = null)
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

    // Destructive commands that mirror a GUI confirm modal. Their SOLE role here is
    // the {"confirm":true} gate: driven via the API they require an explicit confirm
    // (else 409 + no-op) and are awaited to completion so {ok:true} means the effect
    // took. They do NOT decide whether the modal auto-resolves — that is universal
    // (see InvokeCommandOnUiThreadAsync). Defaults to the standard set; overridable
    // via the constructor so tests can exercise the universal never-hang path with a
    // confirm-gated command deliberately absent from this set.
    private static readonly string[] DefaultConfirmGatedCommands = ["mark-done", "rewrite-selected"];

    private readonly HashSet<string> _confirmGatedCommands =
        new(confirmGatedCommands ?? DefaultConfirmGatedCommands, StringComparer.Ordinal);

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

            var destructive = _confirmGatedCommands.Contains(name);

            // SOLE gate of the confirm-gated set: a destructive command requires an
            // explicit {"confirm":true} (the modal's equivalent) — refuse with 409
            // BEFORE invoking when it is absent.
            if (destructive && Json.ReadBool(body, "confirm") != true)
            {
                return (409, Json.Object(("ok", false), ("command", name), ("error", "confirmation required")));
            }

            // Universal never-hang: EVERY resolved command runs inside the VM's
            // pre-confirmed scope, so ConfirmAsync auto-resolves for THIS API flow —
            // no command can stall on the modal, even a future confirm-gated command
            // missing from the set above. (A concurrent human click is a separate
            // async flow and still gets the modal.)
            return await InvokePreConfirmedCommandAsync(name, command, awaitCompletion: destructive);
        }

        if (PropertyActions.Contains(name))
        {
            return InvokePropertyAction(name, body);
        }

        return (404, Json.Object(("ok", false), ("error", "unknown command")));
    }

    /// <summary>
    /// Invokes a resolved <see cref="ICommand"/> inside the VM's pre-confirmed scope
    /// so <c>ConfirmAsync</c> auto-resolves for THIS API flow — the UNIVERSAL
    /// never-hang guarantee: no API-driven command can stall on the confirmation
    /// modal, whether or not it is in the destructive confirm-gated set. (A
    /// concurrent human button click runs in a different async flow and still opens
    /// the modal; the scope is <see cref="AsyncLocal{T}"/>-confined.)
    ///
    /// A destructive command is awaited to real completion
    /// (<paramref name="awaitCompletion"/>) so {ok:true} means its effect took (e.g.
    /// mark-done archived, observable in /state). Other commands keep button-click
    /// fire-and-forget semantics — they return immediately, so a caller should poll
    /// /state to confirm a long-running run's effect.
    /// </summary>
    private async Task<(int Status, string Json)> InvokePreConfirmedCommandAsync(
        string name, ICommand command, bool awaitCompletion)
    {
        await viewModel.InvokePreConfirmedAsync(async () =>
        {
            if (awaitCompletion && command is IAsyncRelayCommand asyncCommand)
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
