using System.Diagnostics;
using VisualRelay.Packaging;

if (args.Length == 0)
{
    PrintUsage();
    return 2;
}

var command = args[0];
switch (command)
{
    case "generate-iconset":
        GenerateIconset();
        return 0;
    case "build-app-bundle":
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("build-app-bundle: missing <publish-dir> argument.");
                PrintUsage();
                return 2;
            }

            var publishDir = Path.GetFullPath(args[1]);
            var outputDir = args.Length >= 3
                ? Path.GetFullPath(args[2])
                : Path.Combine(publishDir, "dist");
            return BuildAppBundle(publishDir, outputDir);
        }
    default:
        Console.Error.WriteLine($"Unknown subcommand: {command}");
        PrintUsage();
        return 2;
}

static void PrintUsage()
{
    Console.Error.WriteLine("usage: VisualRelay.Packaging <command> [args]");
    Console.Error.WriteLine("  generate-iconset");
    Console.Error.WriteLine("  build-app-bundle <publish-dir> [output-dir]");
}

// ── generate-iconset ──────────────────────────────────────────────────────

static void GenerateIconset()
{
    var iconsetDir = Iconsets.ResolveIconsetDir();
    var masterPath = Path.Combine(iconsetDir, Iconsets.MasterName);
    Iconsets.Generate(iconsetDir, masterPath);
}

// ── build-app-bundle ──────────────────────────────────────────────────────

static int BuildAppBundle(string publishDir, string outputDir)
{
    var exeName = Environment.GetEnvironmentVariable("VISUAL_RELAY_APP_EXE") ?? "VisualRelay.App";

    // Validate inputs.
    if (!Directory.Exists(publishDir))
    {
        Console.Error.WriteLine($"build-app-bundle: publish dir not found: '{publishDir}'");
        return 1;
    }

    var exePath = Path.Combine(publishDir, exeName);
    if (!File.Exists(exePath))
    {
        Console.Error.WriteLine($"build-app-bundle: inner executable '{exeName}' not found in {publishDir}");
        Console.Error.WriteLine("  override the name with VISUAL_RELAY_APP_EXE=<name>.");
        return 1;
    }

    foreach (var tool in new[] { "iconutil", "sips" })
    {
        if (FindInPath(tool) is null)
        {
            Console.Error.WriteLine($"build-app-bundle: {tool} not found (macOS built-in required)");
            return 127;
        }
    }

    // 1. Regenerate the iconset from the master, then build the .icns.
    var iconsetDir = Iconsets.ResolveIconsetDir();
    var masterPath = Path.Combine(iconsetDir, Iconsets.MasterName);
    Iconsets.Generate(iconsetDir, masterPath);

    var iconName = "VisualRelay";
    var workIcns = Path.Combine(Path.GetTempPath(), $"{iconName}-{Guid.NewGuid():N}.icns");
    try
    {
        var iconutil = Process.Start(new ProcessStartInfo
        {
            FileName = "iconutil",
            ArgumentList = { "-c", "icns", iconsetDir, "-o", workIcns },
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        iconutil.WaitForExit(60_000);
        if (iconutil.ExitCode != 0)
        {
            Console.Error.WriteLine($"iconutil failed (exit {iconutil.ExitCode}): {iconutil.StandardError.ReadToEnd()}");
            return iconutil.ExitCode;
        }

        // 2. Lay out VisualRelay.app/Contents/{MacOS,Resources}.
        Directory.CreateDirectory(outputDir);
        var outputDirAbs = Path.GetFullPath(outputDir);
        var appDir = Path.Combine(outputDirAbs, "VisualRelay.app");

        // Remove any previous partial bundle.
        if (Directory.Exists(appDir))
            Directory.Delete(appDir, recursive: true);

        var macosDir = Path.Combine(appDir, "Contents", "MacOS");
        var resourcesDir = Path.Combine(appDir, "Contents", "Resources");
        Directory.CreateDirectory(macosDir);
        Directory.CreateDirectory(resourcesDir);

        // Copy the entire publish payload under Contents/MacOS, skipping the
        // output dir itself (when nested inside publish dir) and any existing
        // .app entries.
        foreach (var entry in Directory.EnumerateFileSystemEntries(publishDir))
        {
            var entryAbs = Path.GetFullPath(entry);
            if (string.Equals(entryAbs, outputDirAbs, StringComparison.Ordinal))
                continue;
            var entryName = Path.GetFileName(entry);
            if (string.Equals(entryName, "VisualRelay.app", StringComparison.Ordinal))
                continue;

            var dest = Path.Combine(macosDir, entryName);
            if (Directory.Exists(entry))
                CopyDirectory(entry, dest);
            else
                File.Copy(entry, dest, overwrite: true);
        }

        // Ensure the inner exe is executable.
        var bundledExe = Path.Combine(macosDir, exeName);
        if (File.Exists(bundledExe) && !OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(bundledExe);
            File.SetUnixFileMode(bundledExe,
                mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }

        // 3. Place the .icns.
        File.Copy(workIcns, Path.Combine(resourcesDir, $"{iconName}.icns"), overwrite: true);

        // 4. Write Info.plist.
        var info = Plists.ResolveInfo(exeName);
        Plists.Write(Path.Combine(appDir, "Contents", "Info.plist"), info);

        // 5. Validate the plist if plutil is available (best-effort).
        var plistPath = Path.Combine(appDir, "Contents", "Info.plist");
        if (FindInPath("plutil") is { } plutil)
        {
            var lint = Process.Start(new ProcessStartInfo
            {
                FileName = plutil,
                ArgumentList = { "-lint", plistPath },
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            lint.WaitForExit(10_000);
            if (lint.ExitCode != 0)
            {
                Console.Error.WriteLine($"plutil -lint warning: {lint.StandardError.ReadToEnd()}");
            }
        }

        Console.WriteLine($"build-app-bundle: wrote {appDir}");
        Console.WriteLine($"  CFBundleExecutable={exeName}  CFBundleIconFile={iconName}  id={info.BundleIdentifier}");
        return 0;
    }
    finally
    {
        if (File.Exists(workIcns))
            File.Delete(workIcns);
    }
}

// ── Helpers ───────────────────────────────────────────────────────────────

static string? FindInPath(string command)
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

static void CopyDirectory(string sourceDir, string destDir)
{
    Directory.CreateDirectory(destDir);
    foreach (var entry in Directory.EnumerateFileSystemEntries(sourceDir))
    {
        var name = Path.GetFileName(entry);
        var dest = Path.Combine(destDir, name);
        if (Directory.Exists(entry))
            CopyDirectory(entry, dest);
        else
            File.Copy(entry, dest, overwrite: true);
    }
}
