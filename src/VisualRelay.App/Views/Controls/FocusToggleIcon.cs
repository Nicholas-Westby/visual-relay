using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VisualRelay.App.Views.Controls;

/// <summary>
/// The master Focus/Restore toggle's crisp vector icon: the conventional pair of
/// diagonal "fullscreen" arrows lying on the top-right ↔ bottom-left diagonal.
/// When expanding (focus the task by collapsing the surrounding panels) the two
/// arrows point <em>outward</em> to opposite corners; when contracting (restore
/// the panels) they point <em>inward</em> toward the centre — the same idiom
/// macOS/Material use for enter/exit fullscreen, so it reads instantly in both
/// states. Drawn rather than rendered from the old <c>⤢</c>/<c>⤡</c> Unicode
/// glyphs so it stays crisp and legibly larger than the per-panel chevrons.
/// </summary>
public sealed class FocusToggleIcon : Control
{
    /// <summary>The edge length of the icon, in device-independent pixels — larger than a chevron.</summary>
    internal const double IconSize = 16;

    private const double StrokeWeight = 1.7;

    /// <summary>How far each outer arrow tip sits from the box corner.</summary>
    private const double Inset = 3.0;

    /// <summary>Length of each of the two short arrowhead legs.</summary>
    private const double Head = 4.0;

    /// <summary>Half-gap between the two shafts at the centre so they never merge.</summary>
    private const double Gap = 1.3;

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

        var geometry = IsContracted ? BuildContract() : BuildExpand();
        context.DrawGeometry(null, pen, geometry);
    }

    // Two arrows on the TR↔BL diagonal, tips at the outer corners (focus/expand).
    internal static Geometry BuildExpand()
    {
        const double lo = Inset;
        const double hi = IconSize - Inset;
        const double mid = IconSize / 2.0;

        var g = new StreamGeometry();
        using var ctx = g.Open();
        // Top-right arrow: tip at the corner, legs pointing back (left + down),
        // shaft running in toward the centre.
        Arrow(ctx, tip: new Point(hi, lo),
            legA: new Point(hi - Head, lo), legB: new Point(hi, lo + Head),
            tail: new Point(mid + Gap, mid - Gap));
        // Bottom-left arrow: point reflection of the above through the centre.
        Arrow(ctx, tip: new Point(lo, hi),
            legA: new Point(lo + Head, hi), legB: new Point(lo, hi - Head),
            tail: new Point(mid - Gap, mid + Gap));
        return g;
    }

    // Two arrows on the TR↔BL diagonal, tips at the centre (restore/contract):
    // shafts run out to the corners, arrowheads sit inward.
    internal static Geometry BuildContract()
    {
        const double lo = Inset;
        const double hi = IconSize - Inset;
        const double mid = IconSize / 2.0;

        var g = new StreamGeometry();
        using var ctx = g.Open();
        // Top-right quadrant arrow points inward (down-left toward centre):
        // tip near centre, legs pointing back up + right, shaft out to the corner.
        Arrow(ctx, tip: new Point(mid + Gap, mid - Gap),
            legA: new Point(mid + Gap, mid - Gap - Head), legB: new Point(mid + Gap + Head, mid - Gap),
            tail: new Point(hi, lo));
        // Bottom-left quadrant arrow: point reflection through the centre.
        Arrow(ctx, tip: new Point(mid - Gap, mid + Gap),
            legA: new Point(mid - Gap, mid + Gap + Head), legB: new Point(mid - Gap - Head, mid + Gap),
            tail: new Point(lo, hi));
        return g;
    }

    // One arrow: a diagonal shaft (tip → tail) plus two short arrowhead legs that
    // both spring from the tip. The shaft is its own open figure; the arrowhead
    // traces legA → tip → legB so the apex gets a clean round join.
    private static void Arrow(StreamGeometryContext ctx, Point tip, Point legA, Point legB, Point tail)
    {
        ctx.BeginFigure(tip, isFilled: false);
        ctx.LineTo(tail);
        ctx.EndFigure(false);

        ctx.BeginFigure(legA, isFilled: false);
        ctx.LineTo(tip);
        ctx.LineTo(legB);
        ctx.EndFigure(false);
    }
}
