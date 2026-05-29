namespace VisualRelay.App.Services;

public interface IFolderPicker
{
    Task<string?> PickFolderAsync(CancellationToken cancellationToken = default);
}

