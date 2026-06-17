namespace VisualRelay.App.Services;

/// <summary>
/// Outcome of a file pick. <see cref="ChosenCount"/> is how many entries the
/// user selected; <see cref="Paths"/> are the ones that resolved to a usable
/// local path. They differ when a chosen entry has a non-local backing (some
/// macOS picker sources), letting callers tell "cancelled" (chosen 0) apart
/// from "chosen but unusable" (chosen &gt; 0, paths empty).
/// </summary>
public sealed record FilePickResult(int ChosenCount, IReadOnlyList<string> Paths);

public interface IFilePicker
{
    Task<FilePickResult> PickFilesAsync(CancellationToken cancellationToken = default);
}
