using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VisualRelay.App.Views.Controls;

/// <summary>
/// The four directions a collapse/expand chevron can point.
/// Exposed by the ViewModel per affordance so XAML never deals in glyph strings.
/// </summary>
public enum ChevronDirection
{
    Left,
    Right,
    Up,
    Down,
}

/// <summary>
/// A single drawn vector chevron. Every chevron in the app — header toggles and
/// rail toggles, horizontal collapse and vertical fold — renders from this one
/// control so the pixel size and stroke weight are identical regardless of
/// <see cref="Direction"/>; the shape is optically centered in a fixed box and
/// merely rotated to point Left/Right/Up/Down. Replaces the old mixed-size
/// Unicode triangle glyphs.
/// </summary>
public sealed class ChevronIcon : Control
{
    /// <summary>The one shared chevron stroke geometry (points Right at rest).</summary>
    /// <remarks>
    /// A small open "&gt;" optically centered in a 12×12 box (ink midpoint at x=6,
    /// y=6). Drawn as a stroked polyline so the weight is uniform; rotated for
    /// the other directions. X range: 4–8 (midpoint 6); Y range: 2.5–9.5 (midpoint 6).
    /// </remarks>
    public static readonly Geometry SharedGeometry =
        Geometry.Parse("M 4 2.5 L 8 6 L 4 9.5");

    /// <summary>The fixed edge length of every chevron, in device-independent pixels.</summary>
    public const double IconSize = 12;

    private const double StrokeWeight = 1.6;

    public static readonly StyledProperty<ChevronDirection> DirectionProperty =
        AvaloniaProperty.Register<ChevronIcon, ChevronDirection>(nameof(Direction));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<ChevronIcon, IBrush?>(nameof(Foreground),
            defaultValue: new SolidColorBrush(Color.Parse("#6F7785")));

    static ChevronIcon()
    {
        AffectsRender<ChevronIcon>(DirectionProperty, ForegroundProperty);
    }

    public ChevronIcon()
    {
        Width = IconSize;
        Height = IconSize;
    }

    /// <summary>Which way the chevron points.</summary>
    public ChevronDirection Direction
    {
        get => GetValue(DirectionProperty);
        set => SetValue(DirectionProperty, value);
    }

    /// <summary>Stroke colour of the chevron.</summary>
    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>Degrees to rotate the at-rest (Right-pointing) geometry.</summary>
    private static double AngleFor(ChevronDirection direction) => direction switch
    {
        ChevronDirection.Right => 0,
        ChevronDirection.Down => 90,
        ChevronDirection.Left => 180,
        ChevronDirection.Up => 270,
        _ => 0,
    };

    public override void Render(DrawingContext context)
    {
        var pen = new Pen(Foreground ?? Brushes.Gray, StrokeWeight)
        {
            LineJoin = PenLineJoin.Round,
            LineCap = PenLineCap.Round,
        };

        var center = new Point(IconSize / 2, IconSize / 2);
        using (context.PushTransform(
                   Matrix.CreateTranslation(-center.X, -center.Y)
                   * Matrix.CreateRotation(Matrix.ToRadians(AngleFor(Direction)))
                   * Matrix.CreateTranslation(center.X, center.Y)))
        {
            context.DrawGeometry(null, pen, SharedGeometry);
        }
    }
}
