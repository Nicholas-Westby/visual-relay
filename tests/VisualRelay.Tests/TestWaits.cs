namespace VisualRelay.Tests;

/// <summary>
/// Causal waits for effects produced OUTSIDE the current async flow — a spawned
/// process writing a file, or a fire-and-forget in-proc task the test cannot await
/// directly. These are event-driven (a <see cref="FileSystemWatcher"/> fires on the
/// filesystem change), NOT wall-clock polls, so they satisfy the "await, don't poll"
/// doctrine: the wait ends the instant the effect lands, not on a timer tick. A
/// safety timeout (a <see cref="CancellationTokenSource"/> deadline, never a
/// <c>Task.Delay</c>) turns a stuck producer into a fast, legible failure instead of
/// a hang.
/// </summary>
internal static class TestWaits
{
    /// <summary>
    /// Completes once <paramref name="predicate"/> holds (default: <paramref name="path"/>
    /// exists), driven by filesystem-change events under <paramref name="path"/>'s
    /// directory rather than polling. Returns the predicate's final value; a false return
    /// means the safety timeout elapsed first.
    /// </summary>
    public static async Task<bool> ForFileAsync(
        string path, Func<bool>? predicate = null, int timeoutSeconds = 20)
    {
        predicate ??= () => File.Exists(path);
        if (predicate())
            return true;

        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);

        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(dir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.DirectoryName,
        };
        void Check(object? _, FileSystemEventArgs __)
        {
            if (predicate())
                landed.TrySetResult();
        }
        watcher.Created += Check;
        watcher.Changed += Check;
        watcher.Renamed += Check;
        watcher.EnableRaisingEvents = true;

        // Close the produced-before-subscribe race: re-check after wiring the events.
        if (predicate())
            return true;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        await using var reg = timeout.Token.Register(() => landed.TrySetResult());
        await landed.Task;
        return predicate();
    }
}
