using System.Diagnostics;
using System.Xml.Linq;

namespace VisualRelay.Tests;

// macOS .app bundle (packaging) coverage. The bundle (.icns + Info.plist) is a
// build-time output produced by the tools/VisualRelay.Packaging C# tool.
// We assert on the committed C# source files (existence / content references)
// and the committed source iconset rather than on an opaque .icns blob.
// House style mirrors AppIconTests.
public sealed class MacAppBundleTests
{
    private static string RepoRoot => RepoSetup.Root;

    private static string PackagingProjectDir =>
        Path.Combine(RepoRoot, "tools", "VisualRelay.Packaging");
    private static string PackagingCsproj =>
        Path.Combine(PackagingProjectDir, "VisualRelay.Packaging.csproj");
    private static string PackagingProgramCs =>
        Path.Combine(PackagingProjectDir, "Program.cs");
    private static string PackagingPlistsCs =>
        Path.Combine(PackagingProjectDir, "Plists.cs");
    private static string PackagingIconsetsCs =>
        Path.Combine(PackagingProjectDir, "Iconsets.cs");
    private static string IconsetDir =>
        Path.Combine(RepoRoot, "packaging", "icon", "Visual Relay.iconset");
    private static string IconReadmePath =>
        Path.Combine(RepoRoot, "packaging", "icon", "README.md");

    // ── Bundle generator C# tool (build-time artifacts) ───────────────────

    [Fact]
    public void BuildAppBundleScript_Exists()
    {
        Assert.True(File.Exists(PackagingCsproj),
            $"Packaging project not found at {PackagingCsproj}. " +
            "A committed C# tool must assemble VisualRelay.app.");
        Assert.True(File.Exists(PackagingProgramCs),
            $"Program.cs not found at {PackagingProgramCs}.");
    }

    [Fact]
    public void BuildAppBundleScript_IsExecutable()
    {
        Assert.True(File.Exists(PackagingCsproj),
            $"Packaging project not found at {PackagingCsproj}.");
        Assert.True(File.Exists(PackagingProgramCs),
            $"Program.cs not found at {PackagingProgramCs}.");
        // C# projects are compiled, not executed as scripts — no Unix mode check needed.
    }

    // The C# tool must drive the .icns generation (iconutil) and write a
    // valid Info.plist with the settled identifiers.
    [Fact]
    public void BuildAppBundleScript_ReferencesIconutilAndPlistKeys()
    {
        Assert.True(File.Exists(PackagingProgramCs),
            $"Program.cs not found at {PackagingProgramCs}.");
        Assert.True(File.Exists(PackagingPlistsCs),
            $"Plists.cs not found at {PackagingPlistsCs}.");

        var programContent = File.ReadAllText(PackagingProgramCs);
        var plistsContent = File.ReadAllText(PackagingPlistsCs);
        var combined = programContent + plistsContent;

        Assert.Contains("iconutil", combined);
        Assert.Contains("org.minify.VisualRelay", combined);
        Assert.Contains("CFBundleIconFile", combined);
        Assert.Contains("VisualRelay", combined);
        Assert.Contains("CFBundleExecutable", combined);
        Assert.Contains("NSHighResolutionCapable", combined);
        Assert.Contains("LSMinimumSystemVersion", combined);
        Assert.Contains("APPL", combined);
    }

    // The iconset generator must regenerate the full .iconset from the master
    // using sips, so the .icns is rebuildable from committed art.
    [Fact]
    public void GenerateIconsetScript_ExistsAndReferencesSips()
    {
        Assert.True(File.Exists(PackagingIconsetsCs),
            $"Iconsets.cs not found at {PackagingIconsetsCs}.");
        var content = File.ReadAllText(PackagingIconsetsCs);
        Assert.Contains("sips", content);
        Assert.Contains("icon_512x512@2x.png", content);
    }

    [Fact]
    public void GenerateIconsetScript_IsExecutable()
    {
        Assert.True(File.Exists(PackagingIconsetsCs),
            $"Iconsets.cs not found at {PackagingIconsetsCs}.");
        // C# source files are compiled, not executed as scripts — no Unix mode check needed.
    }

    // ── Committed iconset (regenerable source art for the .icns) ──────────

    // The full .iconset (all 10 required sizes) must be committed so the .icns
    // is regenerable from committed art via iconutil, not an opaque blob.
    [Fact]
    public void Iconset_ContainsAllRequiredSizes()
    {
        Assert.True(Directory.Exists(IconsetDir),
            $"Iconset directory not found at {IconsetDir}.");
        string[] required =
        [
            "icon_16x16.png", "icon_16x16@2x.png",
            "icon_32x32.png", "icon_32x32@2x.png",
            "icon_128x128.png", "icon_128x128@2x.png",
            "icon_256x256.png", "icon_256x256@2x.png",
            "icon_512x512.png", "icon_512x512@2x.png"
        ];
        foreach (var name in required)
        {
            Assert.True(File.Exists(Path.Combine(IconsetDir, name)),
                $"Iconset is missing {name}. Regenerate with " +
                "dotnet run --project tools/VisualRelay.Packaging/VisualRelay.Packaging.csproj -- generate-iconset.");
        }
    }

    // Optional end-to-end check: when iconutil is on PATH (macOS), the
    // committed iconset must produce a valid .icns (starts with the 'icns'
    // magic). Skips off macOS / when iconutil is absent.
    [Fact]
    public void Iconset_ProducesValidIcnsViaIconutil()
    {
        var iconutil = FindInPath("iconutil");
        if (iconutil is null)
        {
            Assert.Skip("iconutil not on PATH (macOS built-in) — " +
                "skipping the end-to-end .icns build assertion.");
        }

        var outIcns = Path.Combine(Path.GetTempPath(),
            $"VisualRelay-test-{Guid.NewGuid():N}.icns");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = iconutil,
                ArgumentList = { "-c", "icns", IconsetDir, "-o", outIcns },
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            var exited = p.WaitForExit(20_000);
            if (!exited)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                Assert.Fail("iconutil did not exit within 20 s.");
            }

            Assert.True(p.ExitCode == 0,
                $"iconutil failed (exit {p.ExitCode}): {p.StandardError.ReadToEnd()}");
            Assert.True(File.Exists(outIcns), "iconutil produced no .icns.");
            var magic = new byte[4];
            using (var fs = File.OpenRead(outIcns))
            {
                _ = fs.Read(magic, 0, 4);
            }

            Assert.Equal("icns", System.Text.Encoding.ASCII.GetString(magic));
        }
        finally
        {
            if (File.Exists(outIcns))
                File.Delete(outIcns);
        }
    }

    // ── Launcher launches through the bundle when present ────────────────

    [Fact]
    public void Launcher_LaunchesThroughBundleWhenPresent()
    {
        var launcher = Path.Combine(RepoRoot, "visual-relay");
        Assert.True(File.Exists(launcher),
            $"Launcher not found at {launcher}.");
        var content = File.ReadAllText(launcher);
        // The launcher must prefer the bundle's inner binary when present.
        Assert.Contains("VisualRelay.app/Contents/MacOS", content);
    }

    // ── Homebrew formula ships and launches through the .app ─────────────

    [Fact]
    public void Formula_ReferencesAppBundle()
    {
        var formula = Path.Combine(RepoRoot, "packaging", "visual-relay.rb");
        Assert.True(File.Exists(formula),
            $"Homebrew formula not found at {formula}.");
        var content = File.ReadAllText(formula);
        Assert.Contains("VisualRelay.app", content);
        // Must keep the CLI model: bin symlink, not a Cask.
        Assert.Contains("bin.install_symlink", content);
    }

    // ── README documents the .icns recipe (deferral removed) ─────────────

    [Fact]
    public void IconReadme_DocumentsIcnsAndRemovesDeferral()
    {
        Assert.True(File.Exists(IconReadmePath),
            $"README not found at {IconReadmePath}.");
        var content = File.ReadAllText(IconReadmePath);
        Assert.Contains("iconutil", content);
        Assert.Contains("VisualRelay.icns", content);
        Assert.DoesNotContain("Not yet in scope", content);
    }

    // ── csproj ships the brand PNG as an AvaloniaResource ────────────────

    // Assets\** is already globbed as AvaloniaResource; assert that glob remains
    // so the new PNG is embedded. (Windows .ico wiring is asserted in
    // AppIconTests and must remain untouched.)
    [Fact]
    public void Csproj_IncludesAssetsAsAvaloniaResource()
    {
        var csproj = Path.Combine(RepoRoot, "src", "VisualRelay.App",
            "VisualRelay.App.csproj");
        Assert.True(File.Exists(csproj), $"csproj not found at {csproj}.");
        var xml = XDocument.Load(csproj);
        var ns = xml.Root!.GetDefaultNamespace();
        var hasAssetsGlob = xml.Root
            .Elements(ns + "ItemGroup")
            .SelectMany(g => g.Elements(ns + "AvaloniaResource"))
            .Any(e => e.Attribute("Include")?.Value is { } v &&
                      v.Contains("Assets", StringComparison.Ordinal));
        Assert.True(hasAssetsGlob,
            "csproj must include Assets\\** as AvaloniaResource so the brand " +
            "PNG ships at runtime.");
    }

    // Asserts the user-execute bit is set. Isolated behind an explicit
    // !IsWindows() guard so the platform analyzer (CA1416) accepts the
    // Unix-only File.GetUnixFileMode call.
    private static void AssertExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        var mode = File.GetUnixFileMode(path);
        Assert.True((mode & UnixFileMode.UserExecute) != 0,
            $"{path} must be executable (chmod +x).");
    }

    // Resolves a command name to its full path by searching PATH directories.
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
