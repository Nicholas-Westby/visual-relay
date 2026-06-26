using VisualRelay.Core.Configuration;
using VisualRelay.Domain;

namespace VisualRelay.Core.Tasks;

public sealed partial class RelayTaskRepository
{
    /// <summary>
    /// Retires a task into the archive WITHOUT running it — for tasks completed
    /// outside Visual Relay. No git commit (matches create/edit/rename).
    /// Returns the final destination path, or null when the repo is unloaded.
    /// </summary>
    public async Task<string?> MarkDoneAsync(RelayTaskItem task, CancellationToken ct = default)
    {
        var loaded = await RelayConfigLoader.TryLoadAsync(RootPath, ct);
        if (loaded.Status != RelayConfigStatus.Loaded)
            return null;

        var retirement = TaskCompletionArchive.RetireAsync(RootPath, loaded.Config, task.Id, task);
        return retirement?.DestinationPath;
    }
}
