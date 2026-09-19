using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// Where <see cref="NonoProfileEnsurer"/> places the guard profile is decided by the
/// <see cref="SandboxHost"/> it is handed, never by the OS the tests run on: the WSL host
/// writes inside the distro, the local host writes the XDG path even while a WSL context
/// is resolved, and only a call handed no host asks what this machine is. The share write
/// is injected, so the distro arm runs here without a share. Two facts resolve a context,
/// each in its own resolution (<see cref="WslContextResolver.IsolateForTests"/>).
/// </summary>
public sealed class NonoProfileEnsurerHostTests
{
    private static readonly WslContext Context =
        new(@"C:\Windows\System32\wsl.exe", "VrNoSuchDistro", "/usr/local/bin/nono", "/home/alice");

    private const string LinuxPlacement = "/home/alice/.config/visual-relay/vr-guard.json";
    private const string SharePlacement = @"\\wsl.localhost\VrNoSuchDistro\home\alice\.config\visual-relay\vr-guard.json";

    [Fact]
    public async Task EnsureAsync_OnTheWslHost_WritesInsideTheDistro_NeverTheLocalPath()
    {
        var xdg = NewXdgRoot();
        var writes = new List<(string Path, string Content)>();
        try
        {
            var written = await NonoProfileEnsurer.EnsureAsync(
                Env(xdg), SandboxHost.Windows(Context), Recorder(writes), TestContext.Current.CancellationToken);

            Assert.Equal(LinuxPlacement, written);
            var write = Assert.Single(writes);
            Assert.Equal(SharePlacement, write.Path);
            Assert.Equal(NonoProfileEnsurer.EmbeddedContent, write.Content);
            Assert.False(Directory.Exists(xdg), "the distro arm must not touch the local config dir");
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(xdg);
        }
    }

    [Fact]
    public async Task EnsureAsync_OnAWindowsHostWithoutADistro_ThrowsTheActionableError_AndWritesNothing()
    {
        var xdg = NewXdgRoot();
        var writes = new List<(string Path, string Content)>();
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NonoProfileEnsurer.EnsureAsync(
                Env(xdg), SandboxHost.Windows(null), Recorder(writes), TestContext.Current.CancellationToken));

            Assert.Contains("no usable WSL2 distro", ex.Message, StringComparison.Ordinal);
            Assert.Contains("visual-relay launch", ex.Message, StringComparison.Ordinal);
            Assert.Empty(writes);
            Assert.False(Directory.Exists(xdg), "a Windows host must never fall back to the local placement");
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(xdg);
        }
    }

    [Fact]
    public async Task LocalHost_IgnoresAResolvedWslOverride()
    {
        var xdg = NewXdgRoot();
        var env = Env(xdg);
        var localPath = Path.Combine(xdg, "visual-relay", "vr-guard.json");
        var writes = new List<(string Path, string Content)>();
        using var wsl = WslContextResolver.IsolateForTests(@override: Context);
        try
        {
            // The override is live: a Windows host resolved now carries it and places the profile in the distro.
            var windows = await SandboxHost.ResolveAsync(isWindows: true, TestContext.Current.CancellationToken);
            Assert.Equal(LinuxPlacement, windows.ProfilePath);

            var written = await NonoProfileEnsurer.EnsureAsync(
                env, SandboxHost.Local, Recorder(writes), TestContext.Current.CancellationToken);

            Assert.Equal(localPath, NonoProfileEnsurer.ResolveProfilePath(env, SandboxHost.Local));
            Assert.Equal(localPath, written);
            Assert.Equal(NonoProfileEnsurer.EmbeddedContent, await File.ReadAllTextAsync(written, TestContext.Current.CancellationToken));
            Assert.Empty(writes);
            Assert.Equal(
                Path.Combine(XdgConfig.ResolveConfigDir(), "visual-relay", "vr-guard.json"), SandboxHost.Local.ProfilePath);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(xdg);
        }
    }

    /// <summary>
    /// No host keeps the old behavior: this machine decides. Off Windows that is the
    /// local host whatever is resolved; on Windows it is the resolved distro, which the
    /// override answers here without probing the real machine.
    /// </summary>
    [Fact]
    public async Task NoHost_IsThisMachine()
    {
        var xdg = NewXdgRoot();
        var env = Env(xdg);
        var expected = OperatingSystem.IsWindows() ? LinuxPlacement : Path.Combine(xdg, "visual-relay", "vr-guard.json");
        var writes = new List<(string Path, string Content)>();
        using var wsl = WslContextResolver.IsolateForTests(@override: Context);
        try
        {
            Assert.Equal(expected, NonoProfileEnsurer.ResolveProfilePath(env));
            Assert.Equal(expected, await NonoProfileEnsurer.EnsureAsync(
                env, host: null, Recorder(writes), TestContext.Current.CancellationToken));
            Assert.Equal(OperatingSystem.IsWindows() ? 1 : 0, writes.Count);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(xdg);
        }
    }

    private static string NewXdgRoot() =>
        Path.Combine(Path.GetTempPath(), "vr-nono-host", Guid.NewGuid().ToString("N"));

    private static DictionaryEnvironmentAccessor Env(string xdg) => new() { ["XDG_CONFIG_HOME"] = xdg };

    private static Func<string, string, CancellationToken, Task> Recorder(List<(string Path, string Content)> writes) =>
        (path, content, _) =>
        {
            writes.Add((path, content));
            return Task.CompletedTask;
        };
}
