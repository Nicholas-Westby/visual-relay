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
    internal const double Inset = 3.0;

    /// <summary>Length of each of the two short arrowhead legs.</summary>
    private const double Head = 4.0;

    /// <summary>Half-gap between the two shafts at the centre so they never merge.</summary>
    private const double Gap = 1.3;

    /// <summary>
    /// Arrowhead leg length in the CONTRACT state: clamped so the furthest barb
    /// lands exactly at <c>hi = IconSize - Inset</c>, keeping all ink within the
    /// same <c>[Inset, IconSize-Inset]</c> box as the EXPAND state.
    /// Value = hi - mid - Gap = (16-3) - 8 - 1.3 = 3.7.
    /// </summary>
    private const double ContractHead = (IconSize - Inset) - (IconSize / 2.0) - Gap;

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
    internal static Geometry BuildExpand() => BuildArrows(ExpandArrowSpecs());

    // Two arrows on the TR↔BL diagonal, tips at the centre (restore/contract).
    internal static Geometry BuildContract() => BuildArrows(ContractArrowSpecs());

    /// <summary>
    /// Arrow point-specs for the EXPAND (outward / focus) state: each arrow's tip is
    /// at an outer corner, legs point back toward the box, shaft runs in to the centre.
    /// Exposed for tests — comparing these against <see cref="ContractArrowSpecs"/> is
    /// how the "distinct shapes" guard verifies the states differ (the two geometries
    /// share a bounding box, and headless hit-testing is unavailable).
    /// </summary>
    internal static IReadOnlyList<(Point Tip, Point LegA, Point LegB, Point Tail)> ExpandArrowSpecs()
    {
        const double lo = Inset;
        const double hi = IconSize - Inset;
        const double mid = IconSize / 2.0;
        return new[]
        {
            (new Point(hi, lo), new Point(hi - Head, lo), new Point(hi, lo + Head), new Point(mid + Gap, mid - Gap)),
            (new Point(lo, hi), new Point(lo + Head, hi), new Point(lo, hi - Head), new Point(mid - Gap, mid + Gap)),
        };
    }

    /// <summary>
    /// Arrow point-specs for the CONTRACT (inward / restore) state: tips near the centre,
    /// shafts run out to the corners, arrowheads sit inward. <c>ContractHead</c> keeps the
    /// furthest barb at <c>hi = IconSize - Inset</c> so all ink stays within the same
    /// <c>[Inset, IconSize-Inset]</c> box as the EXPAND state.
    /// </summary>
    internal static IReadOnlyList<(Point Tip, Point LegA, Point LegB, Point Tail)> ContractArrowSpecs()
    {
        const double lo = Inset;
        const double hi = IconSize - Inset;
        const double mid = IconSize / 2.0;
        return new[]
        {
            (new Point(mid + Gap, mid - Gap), new Point(mid + Gap, mid - Gap - ContractHead), new Point(mid + Gap + ContractHead, mid - Gap), new Point(hi, lo)),
            (new Point(mid - Gap, mid + Gap), new Point(mid - Gap, mid + Gap + ContractHead), new Point(mid - Gap - ContractHead, mid + Gap), new Point(lo, hi)),
        };
    }

    private static Geometry BuildArrows(IReadOnlyList<(Point Tip, Point LegA, Point LegB, Point Tail)> specs)
    {
        var g = new StreamGeometry();
        using var ctx = g.Open();
        foreach (var (tip, legA, legB, tail) in specs)
        {
            Arrow(ctx, tip, legA, legB, tail);
        }

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
