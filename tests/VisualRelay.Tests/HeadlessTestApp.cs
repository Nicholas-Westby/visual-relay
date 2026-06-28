using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;

[assembly: AvaloniaTestApplication(typeof(VisualRelay.Tests.HeadlessTestApp))]

namespace VisualRelay.Tests;

public static class HeadlessTestApp
{
    // The embedded Inter font registered by WithInterFont(); referenced by its
    // collection URI so unresolved families resolve to it (see below).
    private const string InterFamily = "fonts:Inter#Inter";

    public static AppBuilder BuildAvaloniaApp()
    {
        Environment.SetEnvironmentVariable("VR_CONTROL_DISABLE", "1");
        return AppBuilder.Configure<VisualRelay.App.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .WithInterFont()
            // The headless text platform ships no system fonts, so an explicit
            // FontFamily that does not resolve here — e.g. the app's
            // "Menlo,Consolas,monospace" code font, which exists on the real
            // desktop and under the Skia screenshot tool — fell back to a
            // degenerate stub typeface. A wrapping TextBlock using that typeface
            // inside a width-constrained ScrollViewer drives Avalonia 12's text
            // formatter into an infinite empty-line loop (CreateEmptyTextLine),
            // hanging the first layout pass on Window.Show(). Route every
            // unresolved family to the embedded Inter font so layout always has
            // real metrics, matching what production font resolution provides.
            .With(new FontManagerOptions
            {
                DefaultFamilyName = InterFamily,
                FontFallbacks = [new FontFallback { FontFamily = new FontFamily(InterFamily) }],
            });
    }
}
