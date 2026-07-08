using System.Text.RegularExpressions;

namespace VisualRelay.Tests;

/// <summary>
/// Structural guard: the task-card <c>ControlTheme</c> that strips Fluent's
/// square <c>ListBoxItem</c> chrome must be referenced from the
/// <c>QueuePanel</c> list and must contain no pseudo-class setters that
/// would reintroduce square highlights behind the rounded cards.
///
/// These tests catch accidental removal or pollution of the theme so the
/// square chrome cannot silently return (D1, D2).
/// </summary>
public sealed class TaskCardThemeGuardTests
{
    private static string RepoRoot => RepoSetup.Root;
    private static string QueuePanelPath =>
        Path.Combine(RepoRoot, "src", "VisualRelay.App", "Views", "Controls", "QueuePanel.axaml");
    private static string ThemePath =>
        Path.Combine(RepoRoot, "src", "VisualRelay.App", "Styles", "VisualRelayTheme.axaml");

    /// <summary>
    /// The <c>TaskQueueList</c> <c>ListBox</c> in <c>QueuePanel.axaml</c>
    /// must declare <c>ItemContainerTheme</c> so it uses the scoped
    /// <c>TaskCardItemTheme</c> instead of Fluent's default chrome.
    /// </summary>
    [Fact]
    public void QueuePanel_TaskQueueList_DeclaresItemContainerTheme()
    {
        Assert.True(File.Exists(QueuePanelPath),
            $"QueuePanel.axaml not found at {QueuePanelPath}");

        var text = File.ReadAllText(QueuePanelPath);
        var lines = File.ReadAllLines(QueuePanelPath);

        // Find the line containing x:Name="TaskQueueList" and assert it also
        // contains ItemContainerTheme.
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("x:Name=\"TaskQueueList\""))
            {
                Assert.True(lines[i].Contains("ItemContainerTheme"),
                    $"Line {i + 1} of QueuePanel.axaml with x:Name=\"TaskQueueList\" " +
                    "does not declare ItemContainerTheme.\n" +
                    $"Full line: {lines[i].Trim()}");
                return;
            }
        }

        Assert.Fail("Could not find x:Name=\"TaskQueueList\" in QueuePanel.axaml");
    }

    /// <summary>
    /// The <c>TaskCardItemTheme</c> <c>ControlTheme</c> in
    /// <c>VisualRelayTheme.axaml</c> must contain NO
    /// <c>:pointerover</c> / <c>:pressed</c> / <c>:selected</c> pseudo-class
    /// setters — state visuals belong exclusively to the card's bound
    /// properties.
    /// </summary>
    [Fact]
    public void TaskCardItemTheme_HasNoPseudoClassSetters()
    {
        Assert.True(File.Exists(ThemePath),
            $"VisualRelayTheme.axaml not found at {ThemePath}");

        var lines = File.ReadAllLines(ThemePath);

        // Locate the TaskCardItemTheme ControlTheme block.
        var start = -1;
        var end = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("x:Key=\"TaskCardItemTheme\""))
            {
                start = i;
            }

            if (start >= 0 && lines[i].Contains("</ControlTheme>"))
            {
                end = i;
                break;
            }
        }

        Assert.True(start >= 0,
            "TaskCardItemTheme ControlTheme not found in VisualRelayTheme.axaml. " +
            "It must be defined with x:Key=\"TaskCardItemTheme\".");

        Assert.True(end >= 0,
            "TaskCardItemTheme ControlTheme has no closing </ControlTheme> tag.");

        var forbidden = new[] { ":pointerover", ":pressed", ":selected" };
        for (var i = start; i <= end; i++)
        {
            var line = lines[i];
            foreach (var pattern in forbidden)
            {
                if (line.Contains(pattern))
                {
                    Assert.Fail(
                        $"TaskCardItemTheme (line {i + 1}) contains forbidden " +
                        $"pseudo-class '{pattern}'. State visuals belong exclusively " +
                        $"to the card's bound properties.\n" +
                        $"Line: {line.Trim()}");
                }
            }
        }
    }
}
