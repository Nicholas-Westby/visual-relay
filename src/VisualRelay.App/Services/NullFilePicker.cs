namespace VisualRelay.App.Services;

public sealed class NullFilePicker : IFilePicker
{
    public Task<IReadOnlyList<string>> PickFilesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
