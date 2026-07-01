using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace VisualRelay.Tests;

/// <summary>
/// WCAG 2.x contrast tests for the dark theme. The archive day-group header in
/// QueuePanel.axaml used a dim grey (#5A6270) on the panel background (#12151B) —
/// about 2.97:1, below the 4.5:1 AA minimum for its 11px SemiBold text. These
/// tests encode the WCAG relative-luminance / contrast-ratio math and guard the
/// inline foreground literals that render on the panel so a too-dim colour cannot
/// slip back in. Pure math plus a static XAML scan, so plain [Fact]/[Theory] (no
/// Avalonia headless session required).
/// </summary>
public sealed class ContrastTests
{
    /// <summary>WCAG AA minimum contrast ratio for normal-size text.</summary>
    private const double AaNormal = 4.5;

    /// <summary>Border.panel background from VisualRelayTheme.axaml.</summary>
    private const string PanelBackground = "#12151B";

    // ---- WCAG contrast helper --------------------------------------------

    /// <summary>sRGB 8-bit channel to its linear-light component (WCAG formula).</summary>
    private static double Linearize(int channel)
    {
        var c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>Relative luminance of a #RRGGBB colour per WCAG 2.x.</summary>
    private static double RelativeLuminance(string hex)
    {
        var (r, g, b) = ParseRgb(hex);
        return (0.2126 * Linearize(r)) + (0.7152 * Linearize(g)) + (0.0722 * Linearize(b));
    }

    /// <summary>Contrast ratio (1..21) between two #RRGGBB colours.</summary>
    private static double ContrastRatio(string foreground, string background)
    {
        var l1 = RelativeLuminance(foreground);
        var l2 = RelativeLuminance(background);
        var (hi, lo) = l1 >= l2 ? (l1, l2) : (l2, l1);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static (int R, int G, int B) ParseRgb(string hex)
    {
        var h = hex.TrimStart('#');
        return (
            int.Parse(h.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(h.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(h.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    // ---- Helper self-checks + the documented header baseline -------------

    [Fact]
    public void Helper_MatchesKnownWcagReferenceRatios()
    {
        // Black on white is the canonical 21:1; equal colours are 1:1.
        Assert.True(Math.Abs(ContrastRatio("#000000", "#FFFFFF") - 21.0) < 0.01);
        Assert.True(Math.Abs(ContrastRatio("#FFFFFF", "#FFFFFF") - 1.0) < 0.01);
    }

    [Fact]
    public void DimGreyHeaderFailsAa_LightenedHeaderPassesAa()
    {
        // The original archive-header colour is below AA on the panel (the bug)...
        Assert.True(ContrastRatio("#5A6270", PanelBackground) < AaNormal,
            "#5A6270 on #12151B must be below 4.5:1 — the reason the header was unreadable.");
        // ...and the in-palette panelTitle grey used for the fix clears AA.
        Assert.True(ContrastRatio("#9AA3B1", PanelBackground) >= AaNormal,
            "#9AA3B1 on #12151B must meet 4.5:1 — the accessible header colour.");
    }

    // ---- (a) Named inline literals meet AA on their real rendered surface -
    //
    // Reads the live QueuePanel.axaml, so reverting a foreground back to the old
    // dim grey fails here. Covers the archive day header (on the panel) and the
    // STATUS flyout label (on the flyout's own #1B2028 surface, which the on-panel
    // scanner below deliberately does not reach), pinning each element by its Text.
    [Theory]
    [InlineData("{Binding DayHeader}", PanelBackground)]
    [InlineData("STATUS", "#1B2028")]
    public void QueuePanel_NamedText_MeetsAaOnItsSurface(string text, string expectedSurface)
    {
        var element = TextElements(LoadQueuePanel())
            .Single(t => (string?)t.Attribute("Text") == text);

        var foreground = (string?)element.Attribute("Foreground");
        Assert.NotNull(foreground);
        Assert.Matches(HexPattern, foreground!);
        Assert.Equal(expectedSurface, ResolveTextSurface(element)); // renders on this surface

        var ratio = ContrastRatio(foreground!, expectedSurface);
        Assert.True(ratio >= AaNormal,
            $"'{text}' foreground {foreground} on {expectedSurface} is {ratio:F2}:1, below {AaNormal}:1.");
    }

    // ---- (b) Scan QueuePanel.axaml: every on-panel text literal meets AA --

    [Fact]
    public void QueuePanel_EveryInlineTextForegroundOnPanel_MeetsAa()
    {
        var onPanel = OnPanelTextForegrounds().ToList();

        // The scan must actually see the panel text, guarding against a refactor
        // that silently makes it match nothing.
        Assert.NotEmpty(onPanel);

        foreach (var (foreground, element) in onPanel)
        {
            var ratio = ContrastRatio(foreground, PanelBackground);
            Assert.True(ratio >= AaNormal,
                $"Inline Foreground=\"{foreground}\" on {element} renders on the panel " +
                $"({PanelBackground}) at {ratio:F2}:1 — below the {AaNormal}:1 AA floor.");
        }
    }

    // ---- Static XAML scanning --------------------------------------------

    private const string HexPattern = "^#[0-9A-Fa-f]{6}$";
    private static readonly Regex Hex = new(HexPattern, RegexOptions.Compiled);

    private static IEnumerable<(string Foreground, string Element)> OnPanelTextForegrounds()
    {
        foreach (var text in TextElements(LoadQueuePanel()))
        {
            var foreground = (string?)text.Attribute("Foreground");
            if (foreground is null || !Hex.IsMatch(foreground))
            {
                continue; // themed or bound foreground, not an inline literal
            }

            if (ResolveTextSurface(text) == PanelBackground)
            {
                yield return (foreground, DescribeElement(text));
            }
        }
    }

    private static IEnumerable<XElement> TextElements(XElement root) =>
        root.DescendantsAndSelf().Where(e =>
            e.Name.LocalName is "TextBlock" or "SelectableTextBlock");

    /// <summary>
    /// Resolves the hex background a text element renders on by walking ancestors:
    /// the nearest inline Background="#hex" wins (normalised to upper-case so a
    /// lower-case literal cannot dodge the guard); a bound background or a styled
    /// non-"panel" Border surface returns null (not statically resolvable); reaching
    /// the root Border Classes="panel" (or the top) yields the panel background.
    /// A control without an inline Background (e.g. the CommonButton hosting the
    /// expand glyph) is transparent here, so its text is attributed to the surface
    /// behind it, matching how the dark theme paints these controls onto the panel.
    /// </summary>
    private static string? ResolveTextSurface(XElement text)
    {
        for (var el = text.Parent; el is not null; el = el.Parent)
        {
            var background = (string?)el.Attribute("Background");
            if (background is not null)
            {
                return Hex.IsMatch(background) ? background.ToUpperInvariant() : null;
            }

            if (el.Name.LocalName == "Border")
            {
                var classes = ((string?)el.Attribute("Classes"))?
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
                if (classes.Length > 0)
                {
                    return classes is ["panel"] ? PanelBackground : null;
                }
            }
        }

        return PanelBackground;
    }

    private static string DescribeElement(XElement text)
    {
        var t = (string?)text.Attribute("Text");
        return t is null ? text.Name.LocalName : $"{text.Name.LocalName} Text={t}";
    }

    private static XElement LoadQueuePanel()
    {
        var path = Path.Combine(RepoSetup.Root,
            "src", "VisualRelay.App", "Views", "Controls", "QueuePanel.axaml");
        Assert.True(File.Exists(path), $"QueuePanel.axaml not found: {path}");
        return XDocument.Load(path).Root!;
    }
}
