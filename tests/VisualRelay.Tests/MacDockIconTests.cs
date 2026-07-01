using Avalonia.Media.Imaging;
using Avalonia.Platform;
using VisualRelay.App;

namespace VisualRelay.Tests;

// Runtime macOS Dock-icon helper coverage.
//
// House style mirrors AppIconTests: assert file existence / load behaviour /
// Assert.Skip(...) for platform-specific paths. The helper is verified as a safe
// no-op off macOS; the actually-calls-AppKit path is only exercised behind
// OperatingSystem.IsMacOS() and never forced (no Dock may be attached in CI).
[Collection("Headless")]
public sealed class MacDockIconTests
{
    private static string RepoRoot => RepoSetup.Root;

    private static string BrandPngPath =>
        Path.Combine(RepoRoot, "src", "VisualRelay.App", "Assets", "app-icon.png");

    // Off macOS the helper must be a complete no-op and report that it did
    // nothing (returns false).
    [AvaloniaFact]
    public void MacDockIcon_TrySet_IsSafeNoOpOffMac()
    {
        if (OperatingSystem.IsMacOS())
        {
            Assert.Skip("On macOS the AppKit path runs; covered by " +
                nameof(MacDockIcon_TrySet_NeverThrowsOnMac) + " instead.");
        }

        var applied = MacDockIcon.TrySet();
        Assert.False(applied,
            "MacDockIcon.TrySet() must be a no-op returning false off macOS.");
    }

    // On macOS the interop path is best-effort: it must never throw, regardless
    // of whether a Dock is attached. We do not assert it returns true.
    [AvaloniaFact]
    public void MacDockIcon_TrySet_NeverThrowsOnMac()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("AppKit interop only exercised on macOS.");
        }

        // Must not throw; best-effort by contract.
        _ = MacDockIcon.TrySet();
    }

    // The brand PNG asset must exist so NSImage can read it (NSImage will not
    // take the .ico). It ships under Assets/ as an AvaloniaResource.
    [AvaloniaFact]
    public void BrandPng_FileExists()
    {
        Assert.True(File.Exists(BrandPngPath),
            $"Brand PNG not found at {BrandPngPath}. " +
            "A PNG derived from the master must ship under Assets/ for the " +
            "macOS runtime Dock icon (NSImage cannot read the .ico).");
    }

    // The brand PNG must be a real, decodable bitmap (not an empty/placeholder
    // file). Loads it via its avares:// URI through Avalonia's asset loader,
    // exactly as the runtime helper does.
    [AvaloniaFact]
    public void BrandPng_LoadsAsBitmap()
    {
        var uri = new Uri("avares://VisualRelay.App/Assets/app-icon.png");
        using var stream = AssetLoader.Open(uri);
        using var bitmap = new Bitmap(stream);
        Assert.True(bitmap.PixelSize is { Width: > 0, Height: > 0 },
            "Brand PNG must decode to a non-empty bitmap.");
    }
}
