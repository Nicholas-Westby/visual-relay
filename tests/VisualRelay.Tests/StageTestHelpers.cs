namespace VisualRelay.Tests;

/// <summary>
/// Shared helpers for tests that drive a real command on disk.
/// <para>
/// These were consolidated out of six duplicated copies in the Swival runner's
/// test files. The runner is gone; writing an executable fixture is not about
/// it, and several surviving tests still need one.
/// </para>
/// </summary>
internal static class StageTestHelpers
{
    /// <summary>Writes an executable script and returns its path.</summary>
    /// <param name="rootPath">Where to write it.</param>
    /// <param name="name">The file name.</param>
    /// <param name="text">The script body.</param>
    /// <returns>The full path to the written script.</returns>
    public static async Task<string> WriteExecutableAsync(string rootPath, string name, string text)
    {
        var path = Path.Combine(rootPath, name);
        await File.WriteAllTextAsync(path, text);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}
