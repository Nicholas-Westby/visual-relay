using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Metadata;

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
/// automatically applies the matching Avalonia style class to its inner
/// <c>Button</c> so the existing theme selectors (<c>Button.primary</c>,
/// <c>Button.warning</c>, etc.) match without any theme changes.
///
/// When <see cref="Glyph"/> is set the control prepends a small
/// <see cref="TextBlock"/> before the <see cref="Content"/>.
///
/// <see cref="CommonButton"/> uses composition — it contains a single
/// <c>Button</c> via its ControlTheme rather than inheriting from
/// <c>Button</c>.
/// </summary>
public partial class CommonButton : TemplatedControl
{
    /// <summary>
    /// Identifies the <see cref="Content"/> styled property.
    /// Registered on <see cref="CommonButton"/> so the composed control
    /// owns its own Content property (TemplatedControl does not provide one).
    /// </summary>
    public static readonly StyledProperty<object?> ContentProperty =
        AvaloniaProperty.Register<CommonButton, object?>(nameof(Content));

    public static readonly StyledProperty<ButtonAppearance> AppearanceProperty =
        AvaloniaProperty.Register<CommonButton, ButtonAppearance>(
            nameof(Appearance), defaultValue: ButtonAppearance.Default);

    public static readonly StyledProperty<string?> GlyphProperty =
        AvaloniaProperty.Register<CommonButton, string?>(nameof(Glyph));

    public static readonly StyledProperty<System.Windows.Input.ICommand?> CommandProperty =
        AvaloniaProperty.Register<CommonButton, System.Windows.Input.ICommand?>(nameof(Command));

    public static readonly StyledProperty<object?> CommandParameterProperty =
        AvaloniaProperty.Register<CommonButton, object?>(nameof(CommandParameter));

    public static readonly StyledProperty<FlyoutBase?> FlyoutProperty =
        AvaloniaProperty.Register<CommonButton, FlyoutBase?>(nameof(Flyout));

    public static readonly RoutedEvent<RoutedEventArgs> ClickEvent =
        RoutedEvent.Register<CommonButton, RoutedEventArgs>(
            nameof(Click), RoutingStrategies.Bubble);

    private object? _originalContent;
    private bool _isWrapping;
    private Button? _innerButton;

    static CommonButton()
    {
        AppearanceProperty.Changed.AddClassHandler<CommonButton>(OnAppearanceChanged);
        GlyphProperty.Changed.AddClassHandler<CommonButton>(OnGlyphChanged);
    }

    /// <summary>
    /// Gets or sets the content displayed inside the composed button.
    /// </summary>
    [Content]
    public object? Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    public ButtonAppearance Appearance
    {
        get => GetValue(AppearanceProperty);
        set => SetValue(AppearanceProperty, value);
    }

    /// <summary>Optional Unicode glyph (e.g. "⚙") prepended to <see cref="Content"/>.</summary>
    public string? Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public System.Windows.Input.ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    public FlyoutBase? Flyout
    {
        get => GetValue(FlyoutProperty);
        set => SetValue(FlyoutProperty, value);
    }

    /// <summary>Re-raised from the inner Button so XAML Click="…" handlers keep working.</summary>
    public event EventHandler<RoutedEventArgs> Click
    {
        add => AddHandler(ClickEvent, value);
        remove => RemoveHandler(ClickEvent, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        if (_innerButton is not null)
        {
            _innerButton.Click -= OnInnerButtonClick;
            _innerButton = null;
        }

        _innerButton = e.NameScope.Find<Button>("PART_Button");
        if (_innerButton is not null)
        {
            _innerButton.Click += OnInnerButtonClick;
            ApplyAppearanceToInner(Appearance);
            _innerButton.Bind(Button.CommandProperty, this.GetObservable(CommandProperty));
            _innerButton.Bind(Button.CommandParameterProperty, this.GetObservable(CommandParameterProperty));
            _innerButton.Bind(Button.FlyoutProperty, this.GetObservable(FlyoutProperty));
            _originalContent = GetValue(ContentProperty);
            ApplyGlyphToInner();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ContentProperty && !_isWrapping)
        {
            _originalContent = change.NewValue;
            ApplyGlyphToInner();
        }
    }

    private static void OnAppearanceChanged(CommonButton button, AvaloniaPropertyChangedEventArgs e)
    {
        button.ApplyAppearanceToInner((ButtonAppearance)(e.NewValue ?? ButtonAppearance.Default));
    }

    private static void OnGlyphChanged(CommonButton button, AvaloniaPropertyChangedEventArgs e)
    {
        button.ApplyGlyphToInner();
    }

    private void ApplyAppearanceToInner(ButtonAppearance appearance)
    {
        if (_innerButton is null) return;

        _innerButton.Classes.Remove("primary");
        _innerButton.Classes.Remove("warning");
        _innerButton.Classes.Remove("hyperlink");
        _innerButton.Classes.Remove("path");

        switch (appearance)
        {
            case ButtonAppearance.Primary:
                _innerButton.Classes.Add("primary"); break;
            case ButtonAppearance.Warning:
                _innerButton.Classes.Add("warning"); break;
            case ButtonAppearance.Hyperlink:
                _innerButton.Classes.Add("hyperlink"); break;
            case ButtonAppearance.Path:
                _innerButton.Classes.Add("path"); break;
        }
    }

    private void ApplyGlyphToInner()
    {
        if (_innerButton is null) return;

        _isWrapping = true;
        try
        {
            var glyph = Glyph;
            var content = _originalContent;

            if (string.IsNullOrEmpty(glyph) || content is null)
            {
                _innerButton.Content = content;
                return;
            }

            _innerButton.Content = new StackPanel
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
        finally { _isWrapping = false; }
    }

    private static Control CreateContentPresenter(object content)
    {
        if (content is string s)
            return new TextBlock
            {
                Text = s,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };

        return new ContentControl
        {
            Content = content,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
    }

    private void OnInnerButtonClick(object? sender, RoutedEventArgs e)
    {
        RaiseEvent(new RoutedEventArgs(ClickEvent));
    }
}
