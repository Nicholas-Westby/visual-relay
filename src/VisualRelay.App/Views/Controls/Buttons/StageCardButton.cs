using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Metadata;

namespace VisualRelay.App.Views.Controls.Buttons;

/// <summary>
/// A stage card in the stage board.  Automatically applies the
/// <c>"stageButton"</c> style class to its inner <c>Button</c> so the
/// theme selectors (<c>Button.stageButton</c>) match without theme
/// changes.  Rich child content is provided via the existing
/// <c>DataTemplate</c> in <c>StageBoard.axaml</c>.
///
/// <see cref="StageCardButton"/> uses composition — it contains a single
/// <c>Button</c> via its ControlTheme rather than inheriting from
/// <c>Button</c>.
/// </summary>
public partial class StageCardButton : TemplatedControl
{
    /// <summary>
    /// Identifies the <see cref="Content"/> styled property.
    /// Registered on <see cref="StageCardButton"/> so the composed control
    /// owns its own Content property (TemplatedControl does not provide one).
    /// </summary>
    public static readonly StyledProperty<object?> ContentProperty =
        AvaloniaProperty.Register<StageCardButton, object?>(nameof(Content));

    /// <summary>
    /// Identifies the <see cref="Command"/> styled property
    /// (forwarded from the inner <c>Button</c>).
    /// </summary>
    public static readonly StyledProperty<System.Windows.Input.ICommand?> CommandProperty =
        AvaloniaProperty.Register<StageCardButton, System.Windows.Input.ICommand?>(
            nameof(Command));

    /// <summary>
    /// Identifies the <see cref="CommandParameter"/> styled property
    /// (forwarded from the inner <c>Button</c>).
    /// </summary>
    public static readonly StyledProperty<object?> CommandParameterProperty =
        AvaloniaProperty.Register<StageCardButton, object?>(
            nameof(CommandParameter));

    private Button? _innerButton;

    /// <summary>
    /// Gets or sets the content displayed inside the composed button.
    /// </summary>
    [Content]
    public object? Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
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

    /// <summary>
    /// The parameter to pass to <see cref="Command"/>.
    /// </summary>
    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    /// <inheritdoc />
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _innerButton = e.NameScope.Find<Button>("PART_Button");
        if (_innerButton is not null)
        {
            // Forward Content, Command, CommandParameter from outer to inner.
            _innerButton.Bind(Button.ContentProperty, this.GetObservable(ContentProperty));
            _innerButton.Bind(Button.CommandProperty, this.GetObservable(CommandProperty));
            _innerButton.Bind(Button.CommandParameterProperty, this.GetObservable(CommandParameterProperty));

            _innerButton.Classes.Add("stageButton");
        }
    }
}
