using System.Diagnostics;
using System.Xml.Linq;
using VisualRelay.Packaging;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for the <c>tools/VisualRelay.Packaging</c> tool: iconset size table,
/// Info.plist generation, and end-to-end .app bundle assembly.
/// </summary>
public sealed class PackagingToolTests
{
    private static string PackagingCsproj =>
        Path.Combine(RepoSetup.Root, "tools", "VisualRelay.Packaging", "VisualRelay.Packaging.csproj");

    [Fact]
    public void Iconsets_SizeTable_HasNineEntries()
    {
        var table = Iconsets.SizeTable;
        Assert.Equal(9, table.Length);
    }

    [Fact]
    public void Iconsets_SizeTable_ExcludesMaster()
    {
        var table = Iconsets.SizeTable;
        Assert.DoesNotContain(table, e => e.Name == Iconsets.MasterName);
    }

    [Fact]
    public void Iconsets_SizeTable_AllSizesMatchExpected()
    {
        var expected = new (string Name, int Size)[]
        {
            ("icon_16x16.png", 16),
            ("icon_16x16@2x.png", 32),
            ("icon_32x32.png", 32),
            ("icon_32x32@2x.png", 64),
            ("icon_128x128.png", 128),
            ("icon_128x128@2x.png", 256),
            ("icon_256x256.png", 256),
            ("icon_256x256@2x.png", 512),
            ("icon_512x512.png", 512),
        };

        var table = Iconsets.SizeTable;
        Assert.Equal(expected.Length, table.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Name, table[i].Name);
            Assert.Equal(expected[i].Size, table[i].Size);
        }
    }

    [Fact]
    public void Plists_WriteInfoPlist_ContainsAllRequiredKeys()
    {
        var info = new PlistInfo(
            BundleName: "Visual Relay",
            BundleDisplayName: "Visual Relay",
            BundleIdentifier: "org.minify.VisualRelay",
            ExecutableName: "VisualRelay.App",
            IconFileName: "VisualRelay",
            PackageType: "APPL",
            ShortVersionString: "0.1.0",
            BundleVersion: "0.1.0",
            MinMacOSVersion: "11.0");

        var tmpDir = Path.Combine(Path.GetTempPath(), $"vr-plist-test-{Guid.NewGuid():N}");
        var plistPath = Path.Combine(tmpDir, "Info.plist");
        try
        {
            Directory.CreateDirectory(tmpDir);
            Plists.Write(plistPath, info);

            Assert.True(File.Exists(plistPath), "Plists.Write did not produce a file.");

            var xml = XDocument.Load(plistPath);
            var root = xml.Root!;
            Assert.Equal("plist", root.Name.LocalName);
            Assert.Equal("1.0", root.Attribute("version")?.Value);

            var dict = root.Element("dict");
            Assert.NotNull(dict);
            var keys = dict!.Elements("key").Select(k => k.Value).ToHashSet();

            string[] requiredKeys =
            [
                "CFBundleName",
                "CFBundleDisplayName",
                "CFBundleIdentifier",
                "CFBundleExecutable",
                "CFBundleIconFile",
                "CFBundlePackageType",
                "CFBundleShortVersionString",
                "CFBundleVersion",
                "NSHighResolutionCapable",
                "LSMinimumSystemVersion",
            ];

            foreach (var key in requiredKeys)
                Assert.True(keys.Contains(key), $"Missing key: {key}");

            // NSHighResolutionCapable must be <true/>
            var trueElements = dict.Elements("true").ToList();
            Assert.Contains(trueElements,
                t => t.Parent == dict
                     && t.PreviousNode is XElement prev
                     && prev.Name.LocalName == "key"
                     && prev.Value == "NSHighResolutionCapable");

            // Check DOCTYPE is present
            Assert.NotNull(xml.DocumentType);
            Assert.Equal("plist", xml.DocumentType!.Name);
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public void Plists_ResolveInfo_HonorsEnvVarOverrides()
    {
        var env = new Dictionary<string, string?>
        {
            ["VISUAL_RELAY_VERSION"] = "2.0.0",
            ["VISUAL_RELAY_BUNDLE_VERSION"] = "42",
            ["VISUAL_RELAY_MIN_MACOS"] = "13.0",
        };
        static string? Get(string name, Dictionary<string, string?> d) =>
            d.TryGetValue(name, out var v) ? v : null;
        var info = Plists.ResolveInfo("MyApp.Exe", n => Get(n, env));

        Assert.Equal("2.0.0", info.ShortVersionString);
        Assert.Equal("42", info.BundleVersion);
        Assert.Equal("13.0", info.MinMacOSVersion);
        Assert.Equal("MyApp.Exe", info.ExecutableName);
    }

    [Fact]
    public void Plists_ResolveInfo_UsesDefaultsWhenEnvVarsEmpty()
    {
        var info = Plists.ResolveInfo("VisualRelay.App", _ => null);

        Assert.Equal("0.1.0", info.ShortVersionString);
        Assert.Equal("0.1.0", info.BundleVersion);
        Assert.Equal("11.0", info.MinMacOSVersion);
        Assert.Equal("VisualRelay.App", info.ExecutableName);

        Assert.Equal("org.minify.VisualRelay", info.BundleIdentifier);
        Assert.Equal("Visual Relay", info.BundleName);
        Assert.Equal("Visual Relay", info.BundleDisplayName);
        Assert.Equal("VisualRelay", info.IconFileName);
        Assert.Equal("APPL", info.PackageType);
    }

    [Fact]
    public void BuildAppBundle_EndToEnd_ProducesValidBundle()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS .app bundle assembly requires macOS.");
        }

        // Require the native tools
        if (FindInPath("sips") is null
            || FindInPath("iconutil") is null
            || FindInPath("plutil") is null)
        {
            Assert.Skip("sips, iconutil, or plutil not on PATH — skipping bundle end-to-end test.");
        }

        var tmpRoot = Path.Combine(Path.GetTempPath(), $"vr-bundle-test-{Guid.NewGuid():N}");
        var publishDir = Path.Combine(tmpRoot, "publish");
        var outputDir = Path.Combine(tmpRoot, "output");
        try
        {
            // Create a fake publish dir with a fake inner executable
            Directory.CreateDirectory(publishDir);
            var exePath = Path.Combine(publishDir, "VisualRelay.App");
            File.WriteAllText(exePath, "fake executable");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(exePath, UnixFileMode.UserExecute | UnixFileMode.UserRead);

            // Run the build-app-bundle subcommand
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                ArgumentList =
                {
                    "run",
                    "--project",
                    PackagingCsproj,
                    "--",
                    "build-app-bundle",
                    publishDir,
                    outputDir,
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // Set the version env vars so the bundle is reproducible
            psi.Environment["VISUAL_RELAY_VERSION"] = "0.1.0";
            psi.Environment["VISUAL_RELAY_BUNDLE_VERSION"] = "0.1.0";
            psi.Environment["VISUAL_RELAY_MIN_MACOS"] = "11.0";

            using var p = Process.Start(psi)!;
            var exited = p.WaitForExit(60_000);
            if (!exited)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                Assert.Fail("build-app-bundle did not exit within 60 s.");
            }

            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            Assert.True(p.ExitCode == 0,
                $"build-app-bundle failed (exit {p.ExitCode}).\nstdout:\n{stdout}\nstderr:\n{stderr}");

            // Assert bundle layout
            var appDir = Path.Combine(outputDir, "VisualRelay.app");
            Assert.True(Directory.Exists(appDir),
                $"VisualRelay.app not found at {appDir}.");

            var contentsDir = Path.Combine(appDir, "Contents");
            Assert.True(Directory.Exists(Path.Combine(contentsDir, "MacOS")),
                "Contents/MacOS missing.");
            Assert.True(Directory.Exists(Path.Combine(contentsDir, "Resources")),
                "Contents/Resources missing.");
            Assert.True(File.Exists(Path.Combine(contentsDir, "MacOS", "VisualRelay.App")),
                "Inner executable missing from Contents/MacOS.");

            // Info.plist exists and is lint-clean
            var plistPath = Path.Combine(contentsDir, "Info.plist");
            Assert.True(File.Exists(plistPath), "Info.plist missing from bundle.");

            // plutil -lint
            var lint = Process.Start(new ProcessStartInfo
            {
                FileName = "plutil",
                ArgumentList = { "-lint", plistPath },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            lint.WaitForExit(10_000);
            Assert.True(lint.ExitCode == 0,
                $"plutil -lint failed for {plistPath}: {lint.StandardError.ReadToEnd()}");

            // .icns exists
            Assert.True(File.Exists(Path.Combine(contentsDir, "Resources", "VisualRelay.icns")),
                "VisualRelay.icns missing from Contents/Resources.");

            // Assert the tool printed the output path
            Assert.Contains("build-app-bundle: wrote", stdout);
            Assert.Contains("VisualRelay.app", stdout);
        }
        finally
        {
            if (Directory.Exists(tmpRoot))
                Directory.Delete(tmpRoot, recursive: true);
        }
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
