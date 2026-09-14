namespace VisualRelay.Tests;

/// <summary>
/// Host capabilities some tests need that a stock Windows host lacks. Each is probed
/// once per test run and cached, so a test skips only where the capability is really
/// missing and still runs on a Windows host that has it.
/// </summary>
internal static class WindowsTestCapabilities
{
    /// <summary>The skip reason that goes with <see cref="CanCreateSymlinks"/>.</summary>
    public const string NoSymlinks =
        "this Windows host cannot create symlinks (that takes Developer Mode or an elevated run)";

    private static readonly Lazy<bool> SymlinkProbe = new(ProbeSymlinks);

    /// <summary>
    /// True when this process can create a symbolic link. Always true off Windows. On
    /// Windows it takes Developer Mode or the create-symbolic-link privilege of an
    /// elevated run; without either, <see cref="File.CreateSymbolicLink"/> throws
    /// "A required privilege is not held by the client".
    /// </summary>
    public static bool CanCreateSymlinks => SymlinkProbe.Value;

    /// <summary>
    /// Creates one dangling file link in a scratch directory. Windows checks the same
    /// privilege for file and directory links, and a link's target need not exist.
    /// </summary>
    private static bool ProbeSymlinks()
    {
        if (!OperatingSystem.IsWindows())
            return true;

        var dir = Path.Combine(Path.GetTempPath(), "vr-symlink-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.CreateSymbolicLink(Path.Combine(dir, "link"), Path.Combine(dir, "target"));
            return true;
        }
        catch (IOException)
        {
            return false; // the missing privilege surfaces as an IOException
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(dir);
        }
    }
}
