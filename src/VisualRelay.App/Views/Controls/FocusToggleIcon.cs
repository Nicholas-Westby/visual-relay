using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VisualRelay.App.Views.Controls;

/// <summary>
/// The master Focus/Restore toggle's crisp vector icon: two diagonal arrows in
/// a box that point outward to <em>expand</em> (focus the task by collapsing the
/// surrounding panels) and inward to <em>contract</em> (restore the panels).
/// Drawn rather than rendered from the old <c>⤢</c>/<c>⤡</c> Unicode glyphs so it
/// stays crisp and legibly larger than the per-panel chevrons.
/// </summary>
public sealed class FocusToggleIcon : Control
{
    /// <summary>The edge length of the icon, in device-independent pixels — larger than a chevron.</summary>
    private const double IconSize = 16;

    private const double StrokeWeight = 1.7;
    private const double Inset = 2.5;
    private const double Arm = 4.5;

    public static readonly StyledProperty<bool> IsContractedProperty =
        AvaloniaProperty.Register<FocusToggleIcon, bool>(nameof(IsContracted));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<FocusToggleIcon, IBrush?>(nameof(Foreground));

    static FocusToggleIcon()
    {
        AffectsRender<FocusToggleIcon>(IsContractedProperty, ForegroundProperty);
    }

    public FocusToggleIcon()
    {
        Width = IconSize;
        Height = IconSize;
    }

    /// <summary>
    /// When true the arrows point inward (restore/contract); when false they
    /// point outward (focus/expand). Bind to <c>IsFocused</c>.
    /// </summary>
    public bool IsContracted
    {
        get => GetValue(IsContractedProperty);
        set => SetValue(IsContractedProperty, value);
    }

    /// <summary>Stroke colour of the icon.</summary>
    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var pen = new Pen(Foreground ?? Brushes.Gray, StrokeWeight)
        {
            LineJoin = PenLineJoin.Round,
            LineCap = PenLineCap.Round,
        };

        const double lo = Inset;
        const double hi = IconSize - Inset;
        var geometry = IsContracted ? BuildContract(lo, hi) : BuildExpand(lo, hi);
        context.DrawGeometry(null, pen, geometry);
    }

    // Outward arrows: a corner elbow at each of the four corners, pointing out.
    private static Geometry BuildExpand(double lo, double hi)
    {
        var g = new StreamGeometry();
        using var ctx = g.Open();
        Corner(ctx, lo, lo, +Arm, +Arm); // top-left out
        Corner(ctx, hi, lo, -Arm, +Arm); // top-right out
        Corner(ctx, lo, hi, +Arm, -Arm); // bottom-left out
        Corner(ctx, hi, hi, -Arm, -Arm); // bottom-right out
        return g;
    }

    // Inward arrows: a corner elbow set in from each corner, pointing in.
    private static Geometry BuildContract(double lo, double hi)
    {
        var g = new StreamGeometry();
        using var ctx = g.Open();
        Corner(ctx, lo + Arm, lo + Arm, -Arm, -Arm); // toward top-left
        Corner(ctx, hi - Arm, lo + Arm, +Arm, -Arm); // toward top-right
        Corner(ctx, lo + Arm, hi - Arm, -Arm, +Arm); // toward bottom-left
        Corner(ctx, hi - Arm, hi - Arm, +Arm, +Arm); // toward bottom-right
        return g;
    }

    // An L-shaped corner: horizontal arm then vertical arm from the elbow point.
    private static void Corner(StreamGeometryContext ctx, double x, double y, double dx, double dy)
    {
        ctx.BeginFigure(new Point(x + dx, y), isFilled: false);
        ctx.LineTo(new Point(x, y));
        ctx.LineTo(new Point(x, y + dy));
        ctx.EndFigure(false);
    }
}
