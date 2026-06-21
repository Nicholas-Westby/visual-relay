using System.Diagnostics;

namespace VisualRelay.Packaging;

/// <summary>
/// macOS .iconset size table and generation helpers. The .icns is a build-time
/// artifact regenerated from the committed 1024-px master via <c>sips</c>.
/// </summary>
public static class Iconsets
{
    /// <summary>
    /// The master file name inside the iconset directory.
    /// </summary>
    public const string MasterName = "icon_512x512@2x.png";

    /// <summary>
    /// The nine standard .iconset sizes derived from the 1024-px master.
    /// The master itself (<c>icon_512x512@2x.png</c>) is never regenerated.
    /// </summary>
    public static (string Name, int Size)[] SizeTable { get; } =
    [
        ("icon_16x16.png", 16),
        ("icon_16x16@2x.png", 32),
        ("icon_32x32.png", 32),
        ("icon_32x32@2x.png", 64),
        ("icon_128x128.png", 128),
        ("icon_128x128@2x.png", 256),
        ("icon_256x256.png", 256),
        ("icon_256x256@2x.png", 512),
        ("icon_512x512.png", 512),
    ];

    /// <summary>
    /// Walks up from <paramref name="baseDir"/> (or the current directory)
    /// to find the <c>packaging/icon/Visual Relay.iconset</c> directory.
    /// </summary>
    public static string ResolveIconsetDir(string? baseDir = null)
    {
        var dir = new DirectoryInfo(baseDir ?? Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "packaging", "icon", "Visual Relay.iconset");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find packaging/icon/Visual Relay.iconset by walking up from the current directory.");
    }

    /// <summary>
    /// Regenerates all nine derived PNGs from the master using <c>sips</c>.
    /// The master itself is never overwritten.
    /// </summary>
    public static void Generate(string iconsetDir, string masterPath)
    {
        if (!File.Exists(masterPath))
        {
            Console.Error.WriteLine($"generate-iconset: master not found at {masterPath}");
            Environment.Exit(1);
        }

        if (FindInPath("sips") is null)
        {
            Console.Error.WriteLine("generate-iconset: sips not found (macOS built-in required)");
            Environment.Exit(127);
        }

        Console.WriteLine($"generate-iconset: regenerating from {masterPath}");
        foreach (var (name, size) in SizeTable)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sips",
                ArgumentList = { "-z", size.ToString(), size.ToString(), masterPath, "--out", Path.Combine(iconsetDir, name) },
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(30_000);
            if (p.ExitCode != 0)
            {
                var stderr = p.StandardError.ReadToEnd();
                Console.Error.WriteLine($"sips failed for {name} (exit {p.ExitCode}): {stderr}");
                Environment.Exit(p.ExitCode);
            }
            Console.WriteLine($"  {name} ({size}x{size})");
        }
        Console.WriteLine("generate-iconset: done.");
    }

    private static string? FindInPath(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;
        foreach (var dir in path.Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(dir, command);
            if (File.Exists(full))
                return full;
        }
        return null;
    }
}
