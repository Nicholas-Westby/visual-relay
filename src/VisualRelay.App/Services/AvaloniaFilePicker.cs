using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace VisualRelay.App.Services;

public sealed class AvaloniaFilePicker(Window owner) : IFilePicker
{
    public async Task<IReadOnlyList<string>> PickFilesAsync(CancellationToken cancellationToken = default)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "Select attachment files",
            AllowMultiple = true
        });

        // TryGetLocalPath() can return null (non-file backings); OfType<string>
        // drops those and narrows to string without an always-true null check.
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
    }
}
