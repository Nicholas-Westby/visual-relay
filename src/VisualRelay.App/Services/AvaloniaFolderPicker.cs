using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace VisualRelay.App.Services;

public sealed class AvaloniaFolderPicker : IFolderPicker
{
    private readonly Window _owner;

    public AvaloniaFolderPicker(Window owner)
    {
        _owner = owner;
    }

    public async Task<string?> PickFolderAsync(CancellationToken cancellationToken = default)
    {
        var folders = await _owner.StorageProvider.OpenFolderPickerAsync(new()
        {
            Title = "Select Relay root",
            AllowMultiple = false
        });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }
}
