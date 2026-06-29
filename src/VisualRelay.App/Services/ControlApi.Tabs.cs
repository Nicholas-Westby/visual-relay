namespace VisualRelay.App.Services;

public sealed partial class ControlApi
{
    // Tab headers BY IDENTITY, in bound-index order — mirrors the TabControls in
    // ActivityColumn.axaml (bound to ActivityTabIndex) and TaskDetailPanel.axaml
    // (bound to SelectedTabIndex). A select-*-tab command keys on this identity
    // (header name or numeric index), never on a specific screen, so the next
    // /screenshot captures whatever that tab renders.
    private static readonly string[] ActivityTabNames =
        ["Run Log", "Commands", "System", "Input", "Output"];

    private static readonly string[] DetailTabNames =
        ["Markdown", "Context", "Attachments"];

    // Both run on the UI thread (the caller dispatches onto it) so the next
    // /screenshot render observes the new tab — exactly like boost-turns sets a
    // VM property on the UI thread.
    private (int Status, string Json) SelectActivityTab(string name, string? body) =>
        SetTab(name, body, ActivityTabNames, index => viewModel.ActivityTabIndex = index);

    private (int Status, string Json) SelectDetailTab(string name, string? body) =>
        SetTab(name, body, DetailTabNames, index => viewModel.SelectedTabIndex = index);

    private static (int Status, string Json) SetTab(
        string name, string? body, string[] tabNames, Action<int> applyIndex)
    {
        var (index, error) = ResolveTabIndex(body, tabNames);
        if (index is null)
        {
            return (409, Json.Object(("ok", false), ("command", name), ("error", error)));
        }

        applyIndex(index.Value);
        return (200, Json.Object(("ok", true), ("command", name), ("index", index.Value)));
    }

    /// <summary>
    /// Resolves a tab by identity: a case-insensitive header <c>name</c> match
    /// (mirroring select-task's id lookup), or a numeric <c>index</c> within range.
    /// On failure returns a null index plus a PRECISE reason — distinguishing an
    /// unknown name, an out-of-range index, and neither field supplied — so the
    /// caller's 409 tells the operator exactly what to fix.
    /// </summary>
    private static (int? Index, string Error) ResolveTabIndex(string? body, string[] tabNames)
    {
        var requestedName = Json.ReadString(body, "name");
        if (!string.IsNullOrEmpty(requestedName))
        {
            for (var i = 0; i < tabNames.Length; i++)
            {
                if (string.Equals(tabNames[i], requestedName, StringComparison.OrdinalIgnoreCase))
                {
                    return (i, "");
                }
            }

            return (null, $"unknown tab name '{requestedName}'");
        }

        var index = Json.ReadInt(body, "index");
        if (index is null)
        {
            return (null, "tab name or index required");
        }

        return index is >= 0 && index < tabNames.Length
            ? (index, "")
            : (null, $"tab index {index} out of range");
    }
}
