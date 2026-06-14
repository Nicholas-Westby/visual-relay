using System.Diagnostics;
using System.Xml.Linq;

namespace VisualRelay.Tests;

public sealed class AppIconTests
{
    private static string RepoRoot => RepoSetup.Root;

    private static string AppIconPath =>
        Path.Combine(RepoRoot, "src", "VisualRelay.App", "Assets", "app-icon.ico");

    private static string OldLogoPath =>
        Path.Combine(RepoRoot, "src", "VisualRelay.App", "Assets", "avalonia-logo.ico");

    private static string MainWindowAxamlPath =>
        Path.Combine(RepoRoot, "src", "VisualRelay.App", "Views", "MainWindow.axaml");

    private static string CsprojPath =>
        Path.Combine(RepoRoot, "src", "VisualRelay.App", "VisualRelay.App.csproj");

    private static string ThisSourcePath =>
        Path.Combine(RepoRoot, "tests", "VisualRelay.Tests", "AppIconTests.cs");

    // ── FindInPath helper ────────────────────────────────────────────────

    /// <summary>
    /// Resolves a command name to its full path by searching the directories
    /// listed in <c>PATH</c>. Returns <c>null</c> when the command is not found.
    /// </summary>
    private static string? FindInPath(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        foreach (var dir in path.Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var fullPath = Path.Combine(dir, command);
            if (File.Exists(fullPath))
                return fullPath;
        }

        return null;
    }

    // ── FindInPath tests ─────────────────────────────────────────────────

    [Fact]
    public void AppIcon_FindInPath_FindsExistingCommand()
    {
        // Any ubiquitous command on the PATH — bash is required by the
        // repo's own prerequisites (visual-relay uses /bin/bash).
        var result = FindInPath("bash");
        Assert.NotNull(result);
        Assert.True(File.Exists(result));
    }

    [Fact]
    public void AppIcon_FindInPath_ReturnsNullForMissingCommand()
    {
        var result = FindInPath("no-such-command-xyzzy-42");
        Assert.Null(result);
    }

    // ── Static-analysis guard ────────────────────────────────────────────

    /// <summary>
    /// The resolution test must not hardcode a fixed path to <c>magick</c>
    /// (e.g. a Homebrew prefix). It must resolve <c>magick</c> from
    /// <c>PATH</c> instead, and skip when absent.
    /// </summary>
    [Fact]
    public void AppIcon_DoesNotHardcodeMagickPath()
    {
        Assert.True(File.Exists(ThisSourcePath),
            $"Cannot self-inspect: {ThisSourcePath} not found.");

        var source = File.ReadAllText(ThisSourcePath);

        // Split the literal so this assertion does not match itself.
        var forbidden = "/opt/homebrew" + "/bin/magick";
        Assert.DoesNotContain(forbidden, source);
    }

    // ── Icon content tests ───────────────────────────────────────────────

    /// <summary>
    /// The brand icon file must exist in Assets/ so it can be referenced
    /// by the window and the OS/taskbar.
    /// </summary>
    [Fact]
    public void AppIcon_FileExists()
    {
        Assert.True(File.Exists(AppIconPath),
            $"app-icon.ico not found at {AppIconPath}. " +
            "The icon must be extracted from the zip and placed in Assets/.");
    }

    /// <summary>
    /// The .ico must contain multiple resolutions so it renders correctly
    /// in the taskbar (16×16), alt-tab (32×32), file Explorer (48×48, 256×256),
    /// and other OS contexts. Uses ImageMagick identify to inspect the ICO.
    /// Skips when <c>magick</c> is not on PATH rather than erroring.
    /// </summary>
    [Fact]
    public void AppIcon_ContainsMultipleResolutions()
    {
        Assert.True(File.Exists(AppIconPath),
            $"app-icon.ico not found at {AppIconPath} — cannot inspect resolutions.");

        var magickPath = FindInPath("magick");
        if (magickPath is null)
        {
            Assert.Skip(
                "ImageMagick 'magick' not found on PATH. " +
                "Enter the nix devshell (`nix develop`) or install ImageMagick " +
                "to run the ICO resolution assertion.");
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = magickPath,
                ArgumentList = { "identify", AppIconPath },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var exited = process.WaitForExit(10_000);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            Assert.Fail(
                "magick identify did not exit within 10 s — process killed. " +
                "This may indicate a corrupted ICO or a hung ImageMagick process.");
        }

        Assert.True(process.ExitCode == 0,
            $"magick identify failed (exit {process.ExitCode}). " +
            $"stderr: {process.StandardError.ReadToEnd()}");

        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.True(lines.Length >= 5,
            $"Expected at least 5 sub-images in the ICO, got {lines.Length}. " +
            "The icon needs 16, 32, 48, 64, 128, and 256 px sizes for proper OS rendering.\n" +
            $"Output:\n{stdout}");

        // Parse sizes from the identify output. Each line looks like:
        //   app-icon.ico[0] ICO 16x16 16x16=>16x16 ...
        var sizes = new HashSet<int>();
        foreach (var line in lines)
        {
            var match = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)x\1");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var size))
            {
                sizes.Add(size);
            }
        }

        // Minimum set for good OS rendering: 16, 32, 48, 64, 128, 256
        int[] required = [16, 32, 48, 64, 128, 256];
        foreach (var size in required)
        {
            Assert.True(sizes.Contains(size),
                $"ICO missing {size}×{size} resolution. " +
                $"Found sizes: [{string.Join(", ", sizes.OrderBy(s => s))}].");
        }
    }

    /// <summary>
    /// The .csproj must declare the ApplicationIcon so Windows uses it for
    /// the taskbar and the .exe icon in Explorer.
    /// </summary>
    [Fact]
    public void Csproj_HasApplicationIcon()
    {
        Assert.True(File.Exists(CsprojPath),
            $"Project file not found at {CsprojPath}.");

        var xml = XDocument.Load(CsprojPath);
        var ns = xml.Root!.GetDefaultNamespace();

        var applicationIcon = xml.Root
            .Element(ns + "PropertyGroup")
            ?.Element(ns + "ApplicationIcon");

        Assert.NotNull(applicationIcon);
        Assert.Equal(@"Assets\app-icon.ico", applicationIcon.Value);
    }

    /// <summary>
    /// MainWindow.axaml must reference the brand icon instead of the
    /// default Avalonia logo so the window chrome shows the correct icon.
    /// </summary>
    [Fact]
    public void MainWindow_ReferencesAppIcon()
    {
        Assert.True(File.Exists(MainWindowAxamlPath),
            $"MainWindow.axaml not found at {MainWindowAxamlPath}.");

        var content = File.ReadAllText(MainWindowAxamlPath);

        Assert.Contains("Icon=\"/Assets/app-icon.ico\"", content);

        // The old default logo must not be referenced.
        Assert.DoesNotContain("Icon=\"/Assets/avalonia-logo.ico\"", content);
    }

    /// <summary>
    /// The old Avalonia default logo must be removed — it is no longer
    /// referenced by any file and should not clutter Assets/.
    /// </summary>
    [Fact]
    public void OldAvaloniaLogo_Removed()
    {
        Assert.False(File.Exists(OldLogoPath),
            $"The old default icon {OldLogoPath} should have been removed. " +
            "It is replaced by app-icon.ico.");
    }

    // ── WaitForExit timeout guard ─────────────────────────────────────────

    /// <summary>
    /// WaitForExit with a timeout must detect a hung process, return false
    /// within ~15 s wall time, and allow the process to be killed.  Guards
    /// against the unbounded WaitForExit() that would block the suite
    /// indefinitely if magick identify hangs.
    /// </summary>
    [Fact]
    public void AppIcon_WaitForExit_TimeoutKillsHungProcess()
    {
        var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                ArgumentList = { "-c", "sleep 9999" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        p.Start();
        var sw = Stopwatch.StartNew();
        var exited = p.WaitForExit(10_000);
        sw.Stop();

        Assert.False(exited,
            "WaitForExit(10_000) should return false for 'sleep 9999'.");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15),
            $"WaitForExit took {sw.Elapsed.TotalSeconds:F1} s; expected < 15 s.");

        try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
        Assert.True(p.WaitForExit(5_000),
            "Process was not reaped within 5 s after Kill().");
        Assert.True(p.HasExited,
            "HasExited should be true after Kill() + WaitForExit().");
    }
}
