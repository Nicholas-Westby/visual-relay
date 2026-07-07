using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

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
/// and applies the matching Avalonia style class to its inner
/// <c>Button</c> so the existing theme selectors
/// (<c>Button.collapseToggle</c>, <c>Button.focusToggle</c>)
/// match without any theme changes.
///
/// Bind <see cref="ChevronDirection"/> when <see cref="IconStyle"/> is
/// <see cref="IconButtonStyle.CollapseToggle"/>, and
/// <see cref="IsContracted"/> when <see cref="IconStyle"/> is
/// <see cref="IconButtonStyle.FocusToggle"/>.
///
/// <see cref="IconButton"/> uses composition — it contains a single
/// <c>Button</c> via its ControlTheme rather than inheriting from
/// <c>Button</c>.
/// </summary>
public partial class IconButton : TemplatedControl
{
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

    /// <summary>
    /// Identifies the <see cref="Command"/> styled property
    /// (forwarded from the inner <c>Button</c>).
    /// </summary>
    public static readonly StyledProperty<System.Windows.Input.ICommand?> CommandProperty =
        AvaloniaProperty.Register<IconButton, System.Windows.Input.ICommand?>(
            nameof(Command));

    private Button? _innerButton;

    static IconButton()
    {
        IconStyleProperty.Changed.AddClassHandler<IconButton>(OnIconStyleChanged);
    }

    public IconButton()
    {
        ApplyIconStyleToInner(IconStyle);
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

    /// <summary>
    /// The <see cref="System.Windows.Input.ICommand"/> to invoke when the
    /// inner button is clicked.
    /// </summary>
    public System.Windows.Input.ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    /// <inheritdoc />
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _innerButton = e.NameScope.Find<Button>("PART_Button");
        if (_innerButton is not null)
        {
            // Forward Command from outer to inner.
            _innerButton.Bind(Button.CommandProperty, this.GetObservable(CommandProperty));

            // Apply the current icon style (which sets Content + classes on
            // the inner button).
            ApplyIconStyleToInner(IconStyle);
        }
    }

    private static void OnIconStyleChanged(IconButton button, AvaloniaPropertyChangedEventArgs e)
    {
        button.ApplyIconStyleToInner((IconButtonStyle)(e.NewValue ?? IconButtonStyle.CollapseToggle));
    }

    private void ApplyIconStyleToInner(IconButtonStyle style)
    {
        if (_innerButton is null)
            return;

        _innerButton.Classes.Remove("collapseToggle");
        _innerButton.Classes.Remove("focusToggle");

        switch (style)
        {
            case IconButtonStyle.CollapseToggle:
                _innerButton.Classes.Add("collapseToggle");
                var chevron = new ChevronIcon();
                chevron.Bind(ChevronIcon.DirectionProperty,
                    this.GetObservable(ChevronDirectionProperty));
                _innerButton.Content = chevron;
                break;
            case IconButtonStyle.FocusToggle:
                _innerButton.Classes.Add("focusToggle");
                var focusIcon = new FocusToggleIcon();
                focusIcon.Bind(FocusToggleIcon.IsContractedProperty,
                    this.GetObservable(IsContractedProperty));
                _innerButton.Content = focusIcon;
                break;
        }
    }
}
