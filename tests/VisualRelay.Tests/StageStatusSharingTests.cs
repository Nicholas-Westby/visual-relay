using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// <c>StageStatusRecord.Read</c> against a file being replaced. Measured twice on real
/// Windows, an hour and two builds apart: a task's planning read a SIBLING task's
/// status.json while that task's driver was replacing its own, and the read threw
/// "The process cannot access the file … because it is being used by another process"
/// — ERROR_SHARING_VIOLATION — which surfaced as `planning exception` and failed a
/// task that had nothing wrong with it. task-06 died on task-05's file; task-09 on
/// task-07's. Different victim each time, same mechanism.
/// <para>
/// POSIX permits both opens, so these pass here whether or not the fix is present.
/// They are a guard against reintroducing it, not a reproduction; only Windows can
/// witness this one.
/// </para>
/// </summary>
public sealed class StageStatusSharingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("vr-status-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static StageStatusEntry[] Entries(string status) =>
        [new StageStatusEntry(1, "Ideate", status)];

    [Fact]
    public async Task ReadingWhileTheFileIsBeingReplaced_NeverThrows()
    {
        await StageStatusRecord.WriteAsync(_dir, Entries("Done"));

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var writer = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
                await StageStatusRecord.WriteAsync(_dir, Entries("Running"), CancellationToken.None);
        });

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                StageStatusRecord.Read(_dir);
        }));

        // The assertion is that nothing above throws: on Windows an unshared read
        // against a replacing move is the exception this pins.
        await Task.WhenAll([writer, .. readers]);
    }

    /// <summary>
    /// The method says it returns empty when the file is unreadable, and every caller
    /// takes it at its word. It used to catch only malformed JSON, so an IO error
    /// escaped a method documented not to fail.
    /// </summary>
    [Fact]
    public void AnUnreadableFile_IsEmptyRatherThanAThrow()
    {
        Assert.Empty(StageStatusRecord.Read(Path.Combine(_dir, "no-such-task")));

        // A directory where the file should be: openable by neither reader nor writer.
        Directory.CreateDirectory(Path.Combine(_dir, "status.json"));
        Assert.Empty(StageStatusRecord.Read(_dir));
    }
}
