using Avalonia;
using Avalonia.Controls;

namespace VisualRelay.Tests;

/// <summary>
/// Structural guard for the shared confirmation-dialog confirm button.
/// The button is created via the <c>CreateConfirmButton</c> factory and
/// used by both the "Rewrite with AI" flow ("Rewrite and Replace") and
/// the attachment-removal confirmation ("Delete").
///
/// Root cause of the misalignment: the Fluent Button ControlTheme sets
/// <c>VerticalAlignment="Center"</c> on the button itself but does NOT
/// set <c>VerticalContentAlignment</c>, and
/// <see cref="ContentControl.VerticalContentAlignmentProperty"/> has no
/// default in Avalonia 12.0.4, so it falls to
/// <c>default(VerticalAlignment) = Top</c>.  The Cancel button inherits
/// the theme's <c>ButtonPadding</c> (with vertical inset) that masks the
/// Top default; the confirm button's <c>Padding = (12, 0)</c> zeroes the
/// vertical padding and exposes it, gluing the label to the top edge.
/// </summary>
[Collection("Headless")]
public sealed class ConfirmationDialogButtonAlignmentTests
{
    /// <summary>
    /// The confirm button must have <c>VerticalContentAlignment = Center</c>
    /// so its label is vertically centered — matching the adjacent Cancel
    /// button.  All existing horizontal-fit properties must be preserved.
    /// </summary>
    [AvaloniaFact]
    public void ConfirmButton_VerticalContentAlignment_IsCenter()
    {
        var button = App.App.CreateConfirmButton("Rewrite and Replace");

        Assert.Equal(Avalonia.Layout.VerticalAlignment.Center,
            button.VerticalContentAlignment);

        // Preserved properties from the prior horizontal-fit fix (e96a235).
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Center,
            button.HorizontalContentAlignment);
        Assert.Equal(32.0, button.Height);
        Assert.Equal(80.0, button.MinWidth);
        Assert.Equal(new Thickness(12, 0), button.Padding);
        Assert.Equal("Rewrite and Replace", button.Content);
    }
}
