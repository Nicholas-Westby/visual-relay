using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(VisualRelay.Tests.HeadlessTestApp))]

namespace VisualRelay.Tests;

public static class HeadlessTestApp
{
    public static AppBuilder BuildAvaloniaApp()
    {
        Environment.SetEnvironmentVariable("VR_CONTROL_DISABLE", "1");
        return AppBuilder.Configure<VisualRelay.App.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .WithInterFont();
    }
}
