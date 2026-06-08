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
    /// </summary>
    [Fact]
    public void AppIcon_ContainsMultipleResolutions()
    {
        Assert.True(File.Exists(AppIconPath),
            $"app-icon.ico not found at {AppIconPath} — cannot inspect resolutions.");

        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/opt/homebrew/bin/magick",
                ArgumentList = { "identify", AppIconPath },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

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
}
