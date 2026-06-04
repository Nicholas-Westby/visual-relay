using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Reveals an on-disk artifact (a report file or trace directory) in the host
/// OS file manager. <see cref="BuildCommand"/> is pure so command construction
/// can be unit-tested per platform without spawning a process.
/// </summary>
public static class FileReveal
{
    public static (string FileName, IReadOnlyList<string> Arguments) BuildCommand(string path, OSPlatform platform)
    {
        if (platform == OSPlatform.OSX)
        {
            // Finder selects/reveals the file in its containing folder.
            return ("open", new[] { "-R", path });
        }

        if (platform == OSPlatform.Windows)
        {
            // Explorer wants "/select,<path>" as a single token, not two args.
            return ("explorer", new[] { $"/select,{path}" });
        }

        // xdg-open cannot select a file, so open the containing directory.
        var directory = Path.GetDirectoryName(path);
        return ("xdg-open", new[] { string.IsNullOrEmpty(directory) ? path : directory });
    }

    public static void Reveal(string path)
    {
        var platform = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? OSPlatform.OSX
            : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OSPlatform.Windows
            : OSPlatform.Linux;
        var (fileName, arguments) = BuildCommand(path, platform);
        try
        {
            var startInfo = new ProcessStartInfo(fileName) { UseShellExecute = false };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            Process.Start(startInfo);
        }
        catch
        {
            // Launching a file manager is best-effort; never crash the app.
        }
    }
}
