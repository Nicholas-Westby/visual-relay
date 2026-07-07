using System.Text.RegularExpressions;

namespace VisualRelay.Tests;

/// <summary>
/// Structural guard: consumer .axaml files must not set per-instance
/// visual-styling attributes on custom button tags.  Those metrics
/// belong in the theme, defined once per variant.
/// </summary>
public sealed class ButtonThemeGuardTests
{
    private static string RepoRoot => RepoSetup.Root;
    private static string ViewsDir =>
        Path.Combine(RepoRoot, "src", "VisualRelay.App", "Views");
    private static string ButtonsDir =>
        Path.Combine(ViewsDir, "Controls", "Buttons");
    private static string AppAxamlPath =>
        Path.Combine(RepoRoot, "src", "VisualRelay.App", "App.axaml");

    private static readonly string[] CustomButtonTagNames =
        ["CommonButton", "IconButton", "StageCardButton"];

    private static readonly string[] ForbiddenVisualAttributes =
        ["Width", "Height", "FontSize", "Padding", "MinHeight", "MinWidth",
         "Background", "BorderBrush", "Foreground"];

    private static List<string> GetAxamlFiles()
    {
        if (!Directory.Exists(ViewsDir)) return [];
        return Directory.GetFiles(ViewsDir, "*.axaml", SearchOption.AllDirectories)
            .OrderBy(f => f).ToList();
    }

    private static bool IsInButtonsDirectory(string filePath)
    {
        var normalized = Path.GetFullPath(filePath);
        var dir = Path.GetFullPath(ButtonsDir);
        return normalized.StartsWith(dir + Path.DirectorySeparatorChar)
            || normalized == dir;
    }

    private static bool IsCustomButtonTagStart(string line)
    {
        foreach (var name in CustomButtonTagNames)
        {
            if (Regex.IsMatch(line, $@"<buttons:{name}(?:\s|>|$)", RegexOptions.IgnoreCase))
                return true;
        }
        return false;
    }

    private static bool LineClosesTag(string line) =>
        Regex.IsMatch(line, @"(?<!-)\>|/\>");

    private static List<string> FindForbiddenAttributes(string line)
    {
        var found = new List<string>();
        foreach (var attr in ForbiddenVisualAttributes)
        {
            if (Regex.IsMatch(line, $@"\b{attr}="))
                found.Add(attr);
        }
        return found;
    }

    /// <summary>
    /// No <c>CommonButton</c>, <c>IconButton</c>, or
    /// <c>StageCardButton</c> tag in consumer .axaml files may carry
    /// per-instance visual-styling attributes
    /// (<c>Width</c>, <c>Height</c>, <c>FontSize</c>, <c>Padding</c>,
    /// <c>MinHeight</c>, <c>MinWidth</c>, <c>Background</c>,
    /// <c>BorderBrush</c>, <c>Foreground</c>).  Those metrics belong in the
    /// theme, defined once per variant.
    /// </summary>
    [Fact]
    public void NoConsumerSetsVisualStylingOnCustomButtons()
    {
        var violations = new List<string>();
        var files = GetAxamlFiles();

        Assert.True(files.Count > 0,
            $"No .axaml files found under {ViewsDir}. "
            + "The Views directory must exist and contain XAML files.");

        foreach (var file in files)
        {
            if (IsInButtonsDirectory(file)) continue;
            if (Path.GetFullPath(file) == Path.GetFullPath(AppAxamlPath)) continue;

            var lines = File.ReadAllLines(file);
            var relativePath = Path.GetRelativePath(RepoRoot, file);
            var insideCustomButton = false;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (IsCustomButtonTagStart(line))
                {
                    insideCustomButton = true;
                    foreach (var attr in FindForbiddenAttributes(line))
                        violations.Add($"  {relativePath}:{i + 1}  →  {attr}");
                }

                if (insideCustomButton && !IsCustomButtonTagStart(line))
                {
                    foreach (var attr in FindForbiddenAttributes(line))
                        violations.Add($"  {relativePath}:{i + 1}  →  {attr}");
                }

                if (insideCustomButton && LineClosesTag(line))
                    insideCustomButton = false;
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} per-instance visual-styling attribute(s) "
            + "on custom button tags in consumer .axaml files.  These metrics "
            + "belong in the theme, defined once per variant — not tuned per "
            + "instance.  Move each value into the appropriate variant's theme "
            + "definition, or delete it if the themed default already serves.\n\n"
            + $"Violations:\n{string.Join("\n", violations)}");
    }
}
