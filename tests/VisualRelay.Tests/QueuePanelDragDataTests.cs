using Avalonia.Input;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views.Controls;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Regression anchor for the macOS drag crash.
///
/// AppKit requires exactly one pasteboard item per drag image. Avalonia's macOS
/// backend creates one drag image PER <see cref="DataTransferItem"/> and one
/// pasteboard item per item that carries a serializable (non-in-process) format.
/// So every item in the drag payload MUST have a serializable format, or AppKit
/// raises NSGenericException and terminates the process ("N items on the pasteboard,
/// but M drag images"). The first crash had an in-process-only item (0 pasteboard /
/// 1 image); the second had two items (1 pasteboard / 2 images). The payload must be
/// a single item carrying BOTH the in-process reorder format and a serializable
/// representation. (The AppKit termination itself cannot be reproduced headless, so
/// these assert payload composition instead.)
/// </summary>
public sealed class QueuePanelDragDataTests
{
    private static TaskRowViewModel MakeRow(string id = "test-task") =>
        new(new RelayTaskItem(id, $"/tmp/{id}.md", "/tmp", false, []));

    [Fact]
    public void BuildDragData_EveryItemHasASerializableFormat()
    {
        // THE macOS invariant: #drag-images (one per item) must equal #pasteboard-items
        // (one per item that has a serializable format). So every item must carry at
        // least one non-in-process format. This is exactly what the prior two-item
        // payload violated — its in-process-only item produced a drag image with no
        // pasteboard partner, and AppKit terminated the app.
        var data = QueuePanel.BuildDragData(MakeRow("my-task"));

        Assert.NotEmpty(data.Items);
        foreach (var item in data.Items)
        {
            Assert.Contains(item.Formats, f => f.Kind != DataFormatKind.InProcess);
        }
    }

    [Fact]
    public void BuildDragData_ContainsInProcessTaskRowFormat()
    {
        // Cast to IDataTransfer to disambiguate the sync/async Contains extensions.
        // The in-process format is the reorder source of truth; the drop handler keys
        // on it in OnDrop/OnDragOver, so it must remain present (on the same item).
        IDataTransfer data = QueuePanel.BuildDragData(MakeRow("my-task"));

        Assert.True(data.Contains(QueuePanel.TaskRowFormatForTest),
            "DataTransfer must contain the in-process TaskRow format so the drop handler can reorder.");
    }

    [Fact]
    public void BuildDragData_TextPayloadIsRowId()
    {
        IDataTransfer data = QueuePanel.BuildDragData(MakeRow("expected-id"));

        Assert.Equal("expected-id", data.TryGetValue(DataFormat.Text));
    }
}
