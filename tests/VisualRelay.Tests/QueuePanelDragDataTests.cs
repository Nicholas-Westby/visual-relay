using Avalonia.Input;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views.Controls;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Regression anchor for the macOS empty-pasteboard crash.
///
/// Root cause: when the drag DataTransfer contains ONLY an in-process format,
/// Avalonia's macOS backend calls AppKit with zero pasteboard items, which
/// triggers a native NSGenericException that terminates the process before any
/// managed catch can run.
///
/// The fix is to include at least one serializable (non-in-process) format so
/// the OS pasteboard is populated. These tests verify the composition of the
/// drag payload returned by QueuePanel.BuildDragData without driving real pointer
/// input (the AppKit termination cannot be reproduced headless).
/// </summary>
public sealed class QueuePanelDragDataTests
{
    private static TaskRowViewModel MakeRow(string id = "test-task") =>
        new(new RelayTaskItem(id, $"/tmp/{id}.md", "/tmp", false, []));

    [Fact]
    public void BuildDragData_ContainsInProcessTaskRowFormat()
    {
        var row = MakeRow("my-task");

        // Cast to IDataTransfer to avoid the ambiguity between the async and sync
        // Contains extension methods that both resolve from DataTransfer.
        IDataTransfer data = QueuePanel.BuildDragData(row);

        // The in-process format is the reorder source of truth; the drop handler
        // keys on it in OnDrop/OnDragOver.
        Assert.True(data.Contains(QueuePanel.TaskRowFormatForTest),
            "DataTransfer must contain the in-process TaskRow format so the drop handler can reorder.");
    }

    [Fact]
    public void BuildDragData_ContainsSerializableTextFormat()
    {
        var row = MakeRow("my-task");

        IDataTransfer data = QueuePanel.BuildDragData(row);

        // The serializable text item populates the OS pasteboard. Without it,
        // AppKit raises NSGenericException on macOS and terminates the process.
        // (A managed try/catch cannot intercept a native ObjC exception.)
        Assert.True(data.Contains(DataFormat.Text),
            "DataTransfer must contain DataFormat.Text so the macOS pasteboard has ≥1 item.");
    }

    [Fact]
    public void BuildDragData_TextPayloadIsRowId()
    {
        var row = MakeRow("expected-id");

        IDataTransfer data = QueuePanel.BuildDragData(row);

        var value = data.TryGetValue(DataFormat.Text);
        Assert.Equal("expected-id", value);
    }
}
