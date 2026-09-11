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
        new(@"C:\Windows\System32\wsl.exe", "Ubuntu", "/usr/local/bin/nono", "/home/alice");

    [Fact]
    public async Task EnsureInDistro_WritesTheEmbeddedProfileThroughTheShareAndReturnsTheLinuxPath()
    {
        var writes = new List<(string Path, string Content)>();

        var linuxPath = await NonoProfileEnsurer.EnsureInDistroAsync(
            Context, (path, content, _) => { writes.Add((path, content)); return Task.CompletedTask; },
            CancellationToken.None);

        Assert.Equal("/home/alice/.config/visual-relay/vr-guard.json", linuxPath);
        var write = Assert.Single(writes);
        Assert.Equal(@"\\wsl.localhost\Ubuntu\home\alice\.config\visual-relay\vr-guard.json", write.Path);
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

    [Fact]
    public async Task EnsureInDistro_ShareUnreachable_NamesTheAutomountSettingAndThePath()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NonoProfileEnsurer.EnsureInDistroAsync(
            Context, (_, _, _) => throw new IOException("The network path was not found."),
            CancellationToken.None));

        Assert.Contains("[automount]", ex.Message);
        Assert.Contains("wsl.conf", ex.Message);
        Assert.Contains(@"\\wsl.localhost\Ubuntu\home\alice\.config\visual-relay\vr-guard.json", ex.Message);
        Assert.Contains("'Ubuntu'", ex.Message);
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
