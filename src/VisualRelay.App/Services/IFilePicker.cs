namespace VisualRelay.App.Services;

public interface IFilePicker
{
    Task<IReadOnlyList<string>> PickFilesAsync(CancellationToken cancellationToken = default);
}
