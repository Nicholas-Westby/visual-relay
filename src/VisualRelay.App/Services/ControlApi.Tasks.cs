using VisualRelay.Core.Configuration;
using VisualRelay.Core.Tasks;

namespace VisualRelay.App.Services;

public sealed partial class ControlApi
{
    /// <summary>
    /// Authors a task headlessly: body <c>{"title":"…","body":"…"}</c> (body
    /// optional). The GUI has a dialog for this; a script has nothing, and writing
    /// the markdown by hand then calling <c>refresh</c> duplicates rules that live
    /// in the view model (slug derivation, reserved/duplicate names, the nested
    /// layout, the reload that puts the task in the queue). So this drives the very
    /// command the dialog's Create button binds and reports what it chose:
    /// <c>{"ok":true,"id":"&lt;slug&gt;","path":"&lt;markdown path&gt;"}</c>, with the
    /// task already present in /state.tasks[] when the response is written.
    ///
    /// 400 is the caller's mistake about the NAME — no title, or a title whose slug
    /// is empty, unsafe, reserved, or already taken (the view model's own message is
    /// passed through). 409 mirrors a disabled button: the app is busy, or no
    /// project folder is open.
    ///
    /// Runs on the UI thread (its caller dispatches) like every other action here.
    /// </summary>
    private async Task<(int Status, string Json)> InvokeCreateTaskAsync(string name, string? body)
    {
        var title = Json.ReadString(body, "title");
        if (string.IsNullOrWhiteSpace(title))
        {
            return (400, Json.Object(("ok", false), ("command", name), ("error", "title required")));
        }

        // Creating a task mutates the queue the drain is walking, so refuse while a
        // run is in flight. CreateNewTaskCommand itself does not check IsBusy — the
        // dialog is reachable mid-run — but a headless caller has no window telling
        // it a run is on, so the API states the precondition instead.
        if (viewModel.IsBusy)
        {
            return (409, Json.Object(("ok", false), ("command", name), ("error", "disabled")));
        }

        // A headless caller passes the whole task in its own request, so no template
        // applies. The selection is the dialog's, it survives the dialog closing,
        // and the create command copies the selected template's attachments into
        // the new folder — so without this a GUI session's last pick would quietly
        // ship its files with every task the API creates afterwards.
        viewModel.SelectedNewTaskTemplateIndex = -1;
        viewModel.NewTaskTitle = title;
        viewModel.NewTaskBody = Json.ReadString(body, "body") ?? string.Empty;

        var command = viewModel.CreateNewTaskCommand;
        if (!command.CanExecute(null))
        {
            return (409, Json.Object(("ok", false), ("command", name), ("error", "disabled")));
        }

        // Awaited to completion (like the confirm-gated commands) so the file is
        // written and the task list reloaded before the caller sees {ok:true}, and
        // inside the pre-confirmed scope so this can never stall on a modal.
        await viewModel.InvokePreConfirmedAsync(() => command.ExecuteAsync(null));

        // The command reports refusals through NewTaskError rather than throwing,
        // exactly as the dialog's inline error label reads them.
        if (viewModel.NewTaskError is { } error)
        {
            return (400, Json.Object(("ok", false), ("command", name), ("error", error)));
        }

        var slug = RelayTaskWriter.Slugify(title);
        return (200, Json.Object(("ok", true), ("command", name), ("id", slug), ("path", ResolveTaskPath(slug))));
    }

    /// <summary>
    /// The created task's markdown path, taken from the reloaded queue row (the
    /// authoritative location) and falling back to the canonical nested layout when
    /// the row is not listed — as when the archive view is showing.
    /// </summary>
    private string ResolveTaskPath(string slug) =>
        viewModel.Tasks.FirstOrDefault(t => string.Equals(t.Id, slug, StringComparison.Ordinal))?.Task.MarkdownPath
            ?? Path.Combine(viewModel.RootPath, RelayConfigLoader.ReadTasksDir(viewModel.RootPath), slug, $"{slug}.md");
}
