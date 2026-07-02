using Avalonia;
using Avalonia.Controls;

namespace VisualRelay.App.Views.Controls.Buttons;

/// <summary>
/// The standard visual appearance of a <see cref="CommonButton"/>,
/// corresponding to the theme style classes defined in
/// <c>VisualRelayTheme.axaml</c>.
/// </summary>
public enum ButtonAppearance
{
    /// <summary>Blue primary-action button (theme class "primary").</summary>
    Primary = 0,

    /// <summary>Grey default button (no extra style class).</summary>
    Default,

    /// <summary>Yellow warning/pause button (theme class "warning").</summary>
    Warning,

    /// <summary>Transparent blue link (theme class "hyperlink").</summary>
    Hyperlink,

    /// <summary>Dark folder-path button (theme class "path").</summary>
    Path,
}

/// <summary>
/// Every general-purpose text button in the app.  Set
/// <see cref="Appearance"/> to choose the visual variant; the control
/// automatically applies the matching Avalonia style class so the
/// existing theme selectors (<c>Button.primary</c>, <c>Button.warning</c>,
/// etc.) match without any theme changes.
///
/// When <see cref="Glyph"/> is set the control prepends a small
/// <see cref="TextBlock"/> before the <see cref="ContentControl.Content"/>,
/// replacing the old inline-⚙ pattern.
/// </summary>
public partial class CommonButton : Button
{
    // Ensure the Button theme (template, visual states, pointer handling)
    // from the Fluent theme applies exactly as it does to plain Button.
    protected override Type StyleKeyOverride => typeof(Button);
    /// <summary>
    /// Identifies the <see cref="Appearance"/> styled property.
    /// </summary>
    public static readonly StyledProperty<ButtonAppearance> AppearanceProperty =
        AvaloniaProperty.Register<CommonButton, ButtonAppearance>(
            nameof(Appearance),
            defaultValue: ButtonAppearance.Default);

    /// <summary>
    /// Identifies the <see cref="Glyph"/> styled property.
    /// </summary>
    public static readonly StyledProperty<string?> GlyphProperty =
        AvaloniaProperty.Register<CommonButton, string?>(
            nameof(Glyph));

    /// <summary>The original <see cref="ContentControl.Content"/> set by the
    /// consumer, saved so we can re-wrap it when <see cref="Glyph"/> changes.</summary>
    private object? _originalContent;

    /// <summary>Whether we are currently wrapping content for a glyph.</summary>
    private bool _isWrapping;

    static CommonButton()
    {
        AppearanceProperty.Changed.AddClassHandler<CommonButton>(OnAppearanceChanged);
        GlyphProperty.Changed.AddClassHandler<CommonButton>(OnGlyphChanged);
    }

    /// <summary>The visual variant of this button.</summary>
    public ButtonAppearance Appearance
    {
        get => GetValue(AppearanceProperty);
        set => SetValue(AppearanceProperty, value);
    }

    /// <summary>
    /// Optional Unicode glyph (e.g. "⚙") to prepend before the button
    /// <see cref="ContentControl.Content"/>.  When set the control
    /// automatically wraps content in a horizontal <see cref="StackPanel"/>.
    /// </summary>
    public string? Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Capture the raw content before Glyph logic wraps it.
        if (change.Property == ContentProperty && !_isWrapping)
        {
            _originalContent = change.NewValue;
            ApplyGlyph();
        }
    }

    private static void OnAppearanceChanged(CommonButton button, AvaloniaPropertyChangedEventArgs e)
    {
        button.ApplyAppearance((ButtonAppearance)(e.NewValue ?? ButtonAppearance.Default));
    }

    private static void OnGlyphChanged(CommonButton button, AvaloniaPropertyChangedEventArgs e)
    {
        button.ApplyGlyph();
    }

    private void ApplyAppearance(ButtonAppearance appearance)
    {
        Classes.Remove("primary");
        Classes.Remove("warning");
        Classes.Remove("hyperlink");
        Classes.Remove("path");

        switch (appearance)
        {
            case ButtonAppearance.Primary:
                Classes.Add("primary");
                break;
            case ButtonAppearance.Warning:
                Classes.Add("warning");
                break;
            case ButtonAppearance.Hyperlink:
                Classes.Add("hyperlink");
                break;
            case ButtonAppearance.Path:
                Classes.Add("path");
                break;
        }
    }

    private void ApplyGlyph()
    {
        _isWrapping = true;
        try
        {
            var glyph = Glyph;
            var content = _originalContent;

            if (string.IsNullOrEmpty(glyph) || content is null)
            {
                Content = content;
                return;
            }

            Content = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new TextBlock
                    {
                        Text = glyph,
                        FontSize = 14,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    },
                    CreateContentPresenter(content),
                },
            };
        }
        finally
        {
            _isWrapping = false;
        }
    }

    /// <summary>
    /// Wraps a string content value in a <see cref="TextBlock"/> so it
    /// renders; passes through any other object unchanged (it will be
    /// hosted by Avalonia's content presenter inside the StackPanel).
    /// </summary>
    private static Control CreateContentPresenter(object content)
    {
        if (content is string s)
        {
            return new TextBlock
            {
                Text = s,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
        }

        // For non-string content (e.g. a Grid), wrap in a ContentControl
        // so it renders inside the StackPanel.
        return new ContentControl
        {
            Content = content,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
    }
}
