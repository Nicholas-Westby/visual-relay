using Avalonia;

namespace VisualRelay.Tests;

/// <summary>
/// Regression guard for the macOS application-menu name. Avalonia derives the
/// app menu title (the bold first menu, next to the Apple logo) from
/// <see cref="Application.Name"/> "for various platform-specific purposes";
/// left unset it defaults to "Avalonia Application" — which is what showed in
/// the unbundled launch. The headless harness configures the production
/// <c>VisualRelay.App.App</c> (see <c>HeadlessTestApp</c>), so this asserts the
/// real app sets its name to the product name.
/// </summary>
[Collection("Headless")]
public sealed class AppMenuNameTests
{
    [AvaloniaFact]
    public void Application_Name_IsProductName()
    {
        Assert.Equal("Visual Relay", Application.Current?.Name);
    }
}
