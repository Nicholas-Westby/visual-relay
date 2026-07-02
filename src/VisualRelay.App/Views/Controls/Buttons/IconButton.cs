using Avalonia;
using Avalonia.Controls;

namespace VisualRelay.App.Views.Controls.Buttons;

/// <summary>
/// The icon style for an <see cref="IconButton"/>.
/// </summary>
public enum IconButtonStyle
{
    /// <summary>26×26 collapse/expand chevron toggle (theme class "collapseToggle").</summary>
    CollapseToggle,

    /// <summary>34×30 focus/restore toggle (theme class "focusToggle").</summary>
    FocusToggle,
}

/// <summary>
/// An icon-only toggle button that auto-composes the correct vector icon
/// (<see cref="ChevronIcon"/> or <see cref="FocusToggleIcon"/>)
/// and applies the matching Avalonia style class so the existing theme
/// selectors (<c>Button.collapseToggle</c>, <c>Button.focusToggle</c>)
/// match without any theme changes.
///
/// Bind <see cref="ChevronDirection"/> when <see cref="IconStyle"/> is
/// <see cref="IconButtonStyle.CollapseToggle"/>, and
/// <see cref="IsContracted"/> when <see cref="IconStyle"/> is
/// <see cref="IconButtonStyle.FocusToggle"/>.
/// </summary>
public partial class IconButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);
    /// <summary>
    /// Identifies the <see cref="IconStyle"/> styled property.
    /// </summary>
    public static readonly StyledProperty<IconButtonStyle> IconStyleProperty =
        AvaloniaProperty.Register<IconButton, IconButtonStyle>(
            nameof(IconStyle));

    /// <summary>
    /// Identifies the <see cref="ChevronDirection"/> styled property.
    /// Only relevant when <see cref="IconStyle"/> is
    /// <see cref="IconButtonStyle.CollapseToggle"/>.
    /// </summary>
    public static readonly StyledProperty<ChevronDirection> ChevronDirectionProperty =
        AvaloniaProperty.Register<IconButton, ChevronDirection>(
            nameof(ChevronDirection),
            defaultValue: ChevronDirection.Right);

    /// <summary>
    /// Identifies the <see cref="IsContracted"/> styled property.
    /// Only relevant when <see cref="IconStyle"/> is
    /// <see cref="IconButtonStyle.FocusToggle"/>.
    /// </summary>
    public static readonly StyledProperty<bool> IsContractedProperty =
        AvaloniaProperty.Register<IconButton, bool>(
            nameof(IsContracted));

    static IconButton()
    {
        IconStyleProperty.Changed.AddClassHandler<IconButton>(OnIconStyleChanged);
    }

    public IconButton()
    {
        ApplyIconStyle(IconStyle);
    }

    /// <summary>Which icon this button displays.</summary>
    public IconButtonStyle IconStyle
    {
        get => GetValue(IconStyleProperty);
        set => SetValue(IconStyleProperty, value);
    }

    /// <summary>
    /// Direction of the collapse/expand chevron.
    /// Only relevant when <see cref="IconStyle"/> is
    /// <see cref="IconButtonStyle.CollapseToggle"/>.
    /// </summary>
    public ChevronDirection ChevronDirection
    {
        get => GetValue(ChevronDirectionProperty);
        set => SetValue(ChevronDirectionProperty, value);
    }

    /// <summary>
    /// Whether the focus toggle is in the contracted (restore) state.
    /// Only relevant when <see cref="IconStyle"/> is
    /// <see cref="IconButtonStyle.FocusToggle"/>.
    /// </summary>
    public bool IsContracted
    {
        get => GetValue(IsContractedProperty);
        set => SetValue(IsContractedProperty, value);
    }

    private static void OnIconStyleChanged(IconButton button, AvaloniaPropertyChangedEventArgs e)
    {
        button.ApplyIconStyle((IconButtonStyle)(e.NewValue ?? IconButtonStyle.CollapseToggle));
    }

    private void ApplyIconStyle(IconButtonStyle style)
    {
        Classes.Remove("collapseToggle");
        Classes.Remove("focusToggle");

        switch (style)
        {
            case IconButtonStyle.CollapseToggle:
                Classes.Add("collapseToggle");
                var chevron = new ChevronIcon();
                chevron.Bind(ChevronIcon.DirectionProperty,
                    this.GetObservable(ChevronDirectionProperty));
                Content = chevron;
                break;
            case IconButtonStyle.FocusToggle:
                Classes.Add("focusToggle");
                var focusIcon = new FocusToggleIcon();
                focusIcon.Bind(FocusToggleIcon.IsContractedProperty,
                    this.GetObservable(IsContractedProperty));
                Content = focusIcon;
                break;
        }
    }
}
