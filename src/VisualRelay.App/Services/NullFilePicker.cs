namespace VisualRelay.App.Services;

public sealed class NullFilePicker : IFilePicker
{
    public Task<FilePickResult> PickFilesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new FilePickResult(0, []));
}
