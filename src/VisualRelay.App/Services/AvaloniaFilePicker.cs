using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace VisualRelay.App.Services;

public sealed class AvaloniaFilePicker : IFilePicker
{
    private readonly Window _owner;

    public AvaloniaFilePicker(Window owner)
    {
        _owner = owner;
    }

    public async Task<IReadOnlyList<string>> PickFilesAsync(CancellationToken cancellationToken = default)
    {
        var files = await _owner.StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "Select attachment files",
            AllowMultiple = true
        });

        return files.Select(f => f.TryGetLocalPath()!).Where(p => p is not null).ToArray();
    }
}
