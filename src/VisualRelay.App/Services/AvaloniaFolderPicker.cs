using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace VisualRelay.App.Services;

public sealed class AvaloniaFolderPicker(Window owner) : IFolderPicker
{
    public async Task<string?> PickFolderAsync(CancellationToken cancellationToken = default)
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new()
        {
            Title = "Select Relay root",
            AllowMultiple = false
        });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }
}
