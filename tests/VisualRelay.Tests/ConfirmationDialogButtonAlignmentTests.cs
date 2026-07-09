namespace VisualRelay.Tests;

/// <summary>
/// Structural guard for the shared confirmation-dialog confirm button.
/// The button is created via the <c>CreateConfirmButton</c> factory and
/// used by both the "Rewrite with AI" flow ("Rewrite and Replace") and
/// the attachment-removal confirmation ("Delete").
///
/// After the composition refactor, <c>CreateConfirmButton</c> returns a
/// <c>CommonButton</c> with <c>Appearance = Primary</c>.  Per-instance
/// visual metrics (<c>Height</c>, <c>MinWidth</c>, <c>Padding</c>, etc.)
/// live in the theme's <c>Button.primary</c> style and are applied by the
/// inner <c>Button</c> at render time — they are no longer set on the
/// <c>CommonButton</c> instance itself.
/// </summary>
[Collection("Headless")]
public sealed class ConfirmationDialogButtonAlignmentTests
{
    /// <summary>
    /// The confirm button must carry the correct content label so the
    /// confirmation dialog reads naturally.  Per-instance visual metrics
    /// (<c>Height</c>, <c>MinWidth</c>, <c>Padding</c>,
    /// <c>VerticalContentAlignment</c>, etc.) are no longer set on the
    /// instance — they live in the theme's <c>Button.primary</c> style
    /// (<c>VisualRelayTheme.axaml</c>) and are applied by the inner
    /// <c>Button</c> at render time.
    /// </summary>
    [AvaloniaFact]
    public void ConfirmButton_HasCorrectContent()
    {
        var button = App.App.CreateConfirmButton("Rewrite and Replace");

        Assert.Equal("Rewrite and Replace", button.Content);
    }

    /// <summary>
    /// After the centralized-button refactor, <c>CreateConfirmButton</c>
    /// must return a <c>CommonButton</c> (not a raw <c>Button</c>) and its
    /// <c>Appearance</c> must be <c>Primary</c> so it renders as a blue
    /// primary-action button.
    /// </summary>
    [AvaloniaFact]
    public void ConfirmButton_IsCommonButton_WithPrimaryAppearance()
    {
        var button = App.App.CreateConfirmButton("Rewrite and Replace");

        // The factory must return a CommonButton, not a raw Button.
        var type = button.GetType();
        Assert.Equal("CommonButton", type.Name);

        // CommonButton must expose an Appearance property …
        var appearanceProp = type.GetProperty("Appearance");
        Assert.NotNull(appearanceProp);

        // … and it must be set to Primary (the first enum member, value 0).
        var appearanceValue = appearanceProp!.GetValue(button);
        Assert.NotNull(appearanceValue);
        Assert.Equal(0, (int)appearanceValue!);
    }
}
