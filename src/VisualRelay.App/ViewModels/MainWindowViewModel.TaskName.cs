using CommunityToolkit.Mvvm.ComponentModel;
using VisualRelay.Core.Tasks;

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
        string? line;
        while ((line = reader.ReadLine()) is not null)
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
        var firstLine = reader.ReadLine();
        if (firstLine is not null && firstLine.StartsWith("# ", StringComparison.Ordinal))
        {
            var title = firstLine[2..].Trim();
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

        return (fallbackId, markdown);
    }

    private static void MigrateTrackingDictKey(HashSet<string> dict, string oldKey, string newKey)
    {
        if (dict.Remove(oldKey))
        {
            dict.Add(newKey);
        }
    }
}
