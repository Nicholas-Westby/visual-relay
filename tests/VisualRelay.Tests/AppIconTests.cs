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
    private static string PackagingIconDir =>
        Path.Combine(RepoRoot, "packaging", "icon");
    private static string IconMasterPngPath =>
        Path.Combine(PackagingIconDir, "Visual Relay.iconset", "icon_512x512@2x.png");
    private static string IconReadmePath =>
        Path.Combine(PackagingIconDir, "README.md");
    private static string StagingAssetsDir =>
        Path.Combine(RepoRoot, "llm-tasks", "replace-app-icon", "assets");

    // Resolves a command name to its full path by searching PATH directories.
    // Returns null when the command is not found.
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
        // bash is required by the repo's own prerequisites (visual-relay uses /bin/bash).
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

    // The resolution test must not hardcode a fixed path to magick
    // (e.g. a Homebrew prefix). It must resolve magick from PATH instead,
    // and skip when absent.
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

    // The brand icon file must exist in Assets/ so it can be referenced
    // by the window and the OS/taskbar.
    [Fact]
    public void AppIcon_FileExists()
    {
        Assert.True(File.Exists(AppIconPath),
            $"app-icon.ico not found at {AppIconPath}. " +
            "The icon must be extracted from the zip and placed in Assets/.");
    }

    // The .ico must contain multiple resolutions (16, 32, 48, 64, 128, 256)
    // for proper OS rendering. Uses ImageMagick identify; skips when magick
    // is not on PATH.
    [Fact]
    public void AppIcon_ContainsMultipleResolutions()
    {
        Assert.True(File.Exists(AppIconPath),
            $"app-icon.ico not found at {AppIconPath} — cannot inspect resolutions.");
        var magickPath = FindInPath("magick");
        if (magickPath is null)
            Assert.Skip("ImageMagick 'magick' not found on PATH. " +
                "Enter the nix devshell (`nix develop`) or install ImageMagick " +
                "to run the ICO resolution assertion.");

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
            Assert.Fail("magick identify did not exit within 10 s — process killed. " +
                "This may indicate a corrupted ICO or a hung ImageMagick process.");
        }
        Assert.True(process.ExitCode == 0,
            $"magick identify failed (exit {process.ExitCode}). " +
            $"stderr: {process.StandardError.ReadToEnd()}");

        var lines = stdout.Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.True(lines.Length >= 5,
            $"Expected at least 5 sub-images in the ICO, got {lines.Length}. " +
            "The icon needs 16, 32, 48, 64, 128, and 256 px sizes.\n" +
            $"Output:\n{stdout}");

        // Parse sizes from identify output: "app-icon.ico[0] ICO 16x16 16x16=>16x16 ..."
        var sizes = new HashSet<int>();
        foreach (var line in lines)
        {
            var match = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)x\1");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var size))
                sizes.Add(size);
        }
        int[] required = [16, 32, 48, 64, 128, 256];
        foreach (var size in required)
            Assert.True(sizes.Contains(size),
                $"ICO missing {size}×{size} resolution. " +
                $"Found sizes: [{string.Join(", ", sizes.OrderBy(s => s))}].");
    }

    // The .csproj must declare the ApplicationIcon so Windows uses it for
    // the taskbar and the .exe icon in Explorer.
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

    // MainWindow.axaml must reference the brand icon so the window chrome
    // shows the correct icon.
    [Fact]
    public void MainWindow_ReferencesAppIcon()
    {
        Assert.True(File.Exists(MainWindowAxamlPath),
            $"MainWindow.axaml not found at {MainWindowAxamlPath}.");
        var content = File.ReadAllText(MainWindowAxamlPath);
        Assert.Contains("Icon=\"/Assets/app-icon.ico\"", content);
        Assert.DoesNotContain("Icon=\"/Assets/avalonia-logo.ico\"", content);
    }

    // The old Avalonia default logo must be removed — it is no longer
    // referenced and should not clutter Assets/.
    [Fact]
    public void OldAvaloniaLogo_Removed()
    {
        Assert.False(File.Exists(OldLogoPath),
            $"The old default icon {OldLogoPath} should have been removed. " +
            "It is replaced by app-icon.ico.");
    }

    // ── Source artwork: packaging/icon/ ──────────────────────────────────

    [Fact]
    public void IconSourceArtwork_DirectoryExists()
    {
        Assert.True(Directory.Exists(PackagingIconDir),
            $"packaging/icon/ directory not found at {PackagingIconDir}. " +
            "The source artwork must be committed under packaging/icon/.");
    }

    [Fact]
    public void IconSourceArtwork_MasterPngExists()
    {
        Assert.True(File.Exists(IconMasterPngPath),
            $"Master icon PNG not found at {IconMasterPngPath}. " +
            "The 1024×1024 master must be committed under packaging/icon/.");
    }

    // ── Regeneration README ─────────────────────────────────────────────

    [Fact]
    public void IconReadme_Exists()
    {
        Assert.True(File.Exists(IconReadmePath),
            $"README.md not found at {IconReadmePath}. " +
            "A README documenting the icon regeneration command is required.");
    }

    [Fact]
    public void IconReadme_ContainsRegenerationCommand()
    {
        Assert.True(File.Exists(IconReadmePath),
            $"README.md not found at {IconReadmePath} — cannot inspect contents.");
        var content = File.ReadAllText(IconReadmePath);
        Assert.Contains("-define icon:auto-resize=256,128,64,48,32,16", content);
        Assert.Contains("src/VisualRelay.App/Assets/app-icon.ico", content);
    }

    // ── Staging cleanup ─────────────────────────────────────────────────

    // The staging copies under llm-tasks/replace-app-icon/assets/ must be
    // cleaned up — the committed source of truth is packaging/icon/, not
    // the task-folder delivery vehicle.
    [Fact]
    public void StagingAssets_CleanedUp()
    {
        Assert.False(Directory.Exists(StagingAssetsDir),
            $"Staging assets directory still exists at {StagingAssetsDir}. " +
            "The task-folder delivery copies must be removed; " +
            "the committed source of truth is under packaging/icon/.");
    }

    // ── WaitForExit timeout guard ───────────────────────────────────────

    // WaitForExit with a timeout must detect a hung process, return false
    // within ~15 s wall time, and allow the process to be killed.
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
