using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// The Windows arm of <see cref="NonoProfileEnsurer"/>: the embedded profile is
/// written INSIDE the distro (through its UNC share) and the Linux path is what
/// the nono prefix gets, overwrite-always like the Unix arm. The write itself is
/// injected so the arm is exercised here without a UNC share.
/// </summary>
public sealed class NonoProfileEnsurerWslTests
{
    private static readonly WslContext Context =
        new(@"C:\Windows\System32\wsl.exe", "VrNoSuchDistro", "/usr/local/bin/nono", "/home/alice");

    [Fact]
    public async Task EnsureInDistro_WritesTheEmbeddedProfileThroughTheShareAndReturnsTheLinuxPath()
    {
        var writes = new List<(string Path, string Content)>();

        var linuxPath = await NonoProfileEnsurer.EnsureInDistroAsync(
            Context, (path, content, _) => { writes.Add((path, content)); return Task.CompletedTask; },
            CancellationToken.None);

        Assert.Equal("/home/alice/.config/visual-relay/vr-guard.json", linuxPath);
        var write = Assert.Single(writes);
        Assert.Equal(@"\\wsl.localhost\VrNoSuchDistro\home\alice\.config\visual-relay\vr-guard.json", write.Path);
        Assert.Equal(NonoProfileEnsurer.EmbeddedContent, write.Content);
    }

    [Fact]
    public async Task EnsureInDistro_WritesEveryTime_SoTheProfileCanNeverGoStale()
    {
        var writes = 0;
        Task Write(string path, string content, CancellationToken ct) { writes++; return Task.CompletedTask; }

        await NonoProfileEnsurer.EnsureInDistroAsync(Context, Write, CancellationToken.None);
        await NonoProfileEnsurer.EnsureInDistroAsync(Context, Write, CancellationToken.None);

        Assert.Equal(2, writes);
    }

    /// <summary>
    /// Parallel planning starts stages within milliseconds of each other (FreshRSS's two tasks
    /// started 3 ms apart), and measured through the WSL share, two writers released together
    /// on one file failed 100 times in 200 with "being used by another process".
    /// </summary>
    [Fact]
    public async Task EnsureInDistro_StagesStartingTogether_WriteOneAtATime()
    {
        var inside = 0;
        var mostInside = 0;
        var mostInsideGate = new object();
        var anotherArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Write(string path, string content, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref inside);
            if (now > 1) anotherArrived.TrySetResult();
            lock (mostInsideGate) mostInside = Math.Max(mostInside, now);
            // Holds this write open long enough for an unserialized second call to reach it.
            SpinWait.SpinUntil(() => anotherArrived.Task.IsCompleted, TimeSpan.FromMilliseconds(500));
            Interlocked.Decrement(ref inside);
            return Task.CompletedTask;
        }

        await Task.WhenAll(
            Task.Run(() => NonoProfileEnsurer.EnsureInDistroAsync(Context, Write, CancellationToken.None)),
            Task.Run(() => NonoProfileEnsurer.EnsureInDistroAsync(Context, Write, CancellationToken.None)));

        Assert.Equal(1, mostInside);
    }

    [Fact]
    public async Task TheShareWriter_LeavesAMatchingProfileUntouched_AndReplacesADifferentOne()
    {
        var root = Directory.CreateTempSubdirectory("vr-share-").FullName;
        try
        {
            var path = Path.Combine(root, "visual-relay", "vr-guard.json");
            await NonoProfileEnsurer.WriteThroughShareAsync(path, "profile", CancellationToken.None);
            var earlier = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, earlier);

            await NonoProfileEnsurer.WriteThroughShareAsync(path, "profile", CancellationToken.None);
            var untouched = File.GetLastWriteTimeUtc(path);
            await NonoProfileEnsurer.WriteThroughShareAsync(path, "grown profile", CancellationToken.None);

            Assert.Equal(earlier, untouched);
            Assert.Equal("grown profile", await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureInDistro_ShareUnreachable_NamesTheAutomountSettingAndThePath()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NonoProfileEnsurer.EnsureInDistroAsync(
            Context, (_, _, _) => throw new IOException("The network path was not found."),
            CancellationToken.None));

        Assert.Contains("[automount]", ex.Message);
        Assert.Contains("wsl.conf", ex.Message);
        Assert.Contains(@"\\wsl.localhost\VrNoSuchDistro\home\alice\.config\visual-relay\vr-guard.json", ex.Message);
        Assert.Contains("'VrNoSuchDistro'", ex.Message);
        Assert.Contains("The network path was not found.", ex.Message);
        Assert.IsType<IOException>(ex.InnerException);
    }

    [Fact]
    public async Task EnsureInDistro_Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NonoProfileEnsurer.EnsureInDistroAsync(
            Context, (_, _, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }, cts.Token));
    }
}
