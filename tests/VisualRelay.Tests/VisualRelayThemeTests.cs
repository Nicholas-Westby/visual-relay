namespace VisualRelay.Tests;

public sealed class VisualRelayThemeTests
{
    [Fact]
    public void Theme_ContainsHyperlinkButtonStyle()
    {
        var themePath = Path.Combine(RepoSetup.Root,
            "src", "VisualRelay.App", "Styles", "VisualRelayTheme.axaml");
        Assert.True(File.Exists(themePath), $"Theme file not found: {themePath}");

        var content = File.ReadAllText(themePath);

        // The theme must define a Button.hyperlink style so the 5
        // SettingsPanel "Get a key" buttons get transparent-background,
        // no-border, blue-foreground, pointer-cursor styling.
        Assert.Contains(@"Selector=""Button.hyperlink""", content);
    }
}
