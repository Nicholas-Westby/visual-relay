namespace VisualRelay.App.Services;

public sealed class NullFolderPicker : IFolderPicker
{
    public Task<string?> PickFolderAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}

