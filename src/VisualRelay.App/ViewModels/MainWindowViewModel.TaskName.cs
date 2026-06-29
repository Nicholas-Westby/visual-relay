using CommunityToolkit.Mvvm.ComponentModel;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty]
    private string _selectedTaskName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveEditCommand))]
    private string _editTitleBuffer = string.Empty;

    /// <summary>
    /// Extracts the human-readable title from the first <c># Title</c> line
    /// in the markdown. Falls back to <paramref name="fallbackId"/> when no
    /// heading line exists.
    /// </summary>
    private static string ExtractTitleFromMarkdown(string markdown, string fallbackId)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return fallbackId;
        }

        var reader = new StringReader(markdown);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                var title = line[2..].Trim();
                return string.IsNullOrEmpty(title) ? fallbackId : title;
            }
        }

        return fallbackId;
    }

    /// <summary>
    /// Splits markdown into a title (first <c># Title</c> line) and the remaining
    /// body. When no <c># </c> line is found, falls back to <paramref name="fallbackId"/>
    /// as the title and returns the entire markdown as the body.
    /// </summary>
    private static (string Title, string Body) SplitMarkdownTitle(string markdown, string fallbackId)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return (fallbackId, string.Empty);
        }

        var reader = new StringReader(markdown);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                var title = line[2..].Trim();
                if (string.IsNullOrEmpty(title))
                {
                    title = fallbackId;
                }

                var remaining = reader.ReadToEnd();
                if (remaining.StartsWith('\n'))
                {
                    remaining = remaining[1..];
                }

                return (title, remaining);
            }
        }

        return (fallbackId, markdown);
    }

    private static void MigrateTrackingDictKey(HashSet<string> dict, string oldKey, string newKey)
    {
        if (dict.Remove(oldKey))
        {
            dict.Add(newKey);
        }
    }

    private static void MigrateDictKey<TValue>(Dictionary<string, TValue> dict, string oldKey, string newKey)
    {
        if (dict.Remove(oldKey, out var value))
        {
            dict[newKey] = value;
        }
    }

    /// <summary>
    /// Re-keys every id-keyed tracking map in the view-model when a task is
    /// renamed. Call this from <see cref="SaveEditAsync"/> after the rename
    /// succeeds and before discarding the rewrite undo for the new id.
    /// </summary>
    private void RekeyTaskId(string oldId, string newId)
    {
        MigrateTrackingDictKey(_boostedTaskIds, oldId, newId);
        MigrateDictKey(_liveEventsByTask, oldId, newId);
        MigrateDictKey(_liveTraceEntriesByTask, oldId, newId);
        MigrateTrackingDictKey(_runningTaskIds, oldId, newId);
        MigrateDictKey(_runningStageNumbers, oldId, newId);
        MigrateDictKey(_runningStageNames, oldId, newId);
        MigrateDictKey(_taskElapsed, oldId, newId);
        MigrateTrackingDictKey(_rewritingTaskIds, oldId, newId);
        MigrateDictKey(_rewriteStartedAt, oldId, newId);
        MigrateDictKey(_rewriteCts, oldId, newId);
        _rewriteUndo.Rekey(oldId, newId);
    }
}
