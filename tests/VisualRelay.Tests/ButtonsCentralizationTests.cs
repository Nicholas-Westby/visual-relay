using System.Text.RegularExpressions;

namespace VisualRelay.Tests;

/// <summary>
/// Structural guard: every <c>&lt;Button</c> in XAML and every <c>new Button</c>
/// in C# code-behind must live inside the central Buttons directory
/// (<c>src/VisualRelay.App/Views/Controls/Buttons/</c>).  The only exceptions
/// are <c>App.axaml</c> (which may reference the base type in a style) and
/// <c>App.axaml.cs</c> (for the confirmation-dialog factory + Cancel button).
///
/// These tests catch raw button usage before it can scatter across the UI,
/// enforcing the centralized button component system.
/// </summary>
public sealed class ButtonsCentralizationTests
{
    private static string RepoRoot => RepoSetup.Root;
    private static string ViewsDir =>
        Path.Combine(RepoRoot, "src", "VisualRelay.App", "Views");
    private static string ButtonsDir =>
        Path.Combine(ViewsDir, "Controls", "Buttons");
    private static string AppAxamlPath =>
        Path.Combine(RepoRoot, "src", "VisualRelay.App", "App.axaml");
    private static string AppAxamlCsPath =>
        Path.Combine(RepoRoot, "src", "VisualRelay.App", "App.axaml.cs");

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all .axaml files under the Views directory, including
    /// subdirectories.
    /// </summary>
    private static List<string> GetAxamlFiles()
    {
        if (!Directory.Exists(ViewsDir))
            return [];
        return Directory.GetFiles(ViewsDir, "*.axaml", SearchOption.AllDirectories)
            .OrderBy(f => f)
            .ToList();
    }

    /// <summary>
    /// Returns all .cs files under the Views directory, including
    /// subdirectories.
    /// </summary>
    private static List<string> GetCsFiles()
    {
        if (!Directory.Exists(ViewsDir))
            return [];
        return Directory.GetFiles(ViewsDir, "*.cs", SearchOption.AllDirectories)
            .OrderBy(f => f)
            .ToList();
    }

    /// <summary>
    /// Returns whether <paramref name="filePath"/> is inside the central
    /// Buttons directory (or that directory itself).
    /// </summary>
    private static bool IsInButtonsDirectory(string filePath)
    {
        var normalized = Path.GetFullPath(filePath);
        var buttonsDir = Path.GetFullPath(ButtonsDir);
        return normalized.StartsWith(buttonsDir + Path.DirectorySeparatorChar)
            || normalized == buttonsDir;
    }

    /// <summary>
    /// Reads a file and finds every line that contains a <c>&lt;Button</c>
    /// opening tag (case-insensitive), returning line-numbered matches.
    /// Excludes self-closing <c>&lt;Button .../&gt;</c> only when the match
    /// is part of a style definition (<c>&lt;Style Selector="Button"</c>
    /// etc.).
    /// </summary>
    private static List<(int Line, string Text)> FindRawButtonTags(string filePath)
    {
        var results = new List<(int, string)>();
        var lines = File.ReadAllLines(filePath);
        // Match a <Button opening tag: <Button or <Button  (with at least one
        // space or > after "Button" so we don't match <Button. (style class
        // selectors in theme files which use Button.primary etc. as the
        // Selector value).
        var regex = new Regex(@"<Button(?:\s|>)", RegexOptions.IgnoreCase);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (regex.IsMatch(line))
            {
                // Exclude style-definition lines: <Style Selector="Button…"
                // (but NOT <Style Selector="Button.primary"> — those don't
                // match the regex because of the dot).
                // Also exclude lines that are inside a <!-- comment, but those
                // are rare; skip for now.
                results.Add((i + 1, line.Trim()));
            }
        }

        return results;
    }

    /// <summary>
    /// Reads a .cs file and finds every line that contains <c>new Button</c>
    /// (or <c>new Button(</c>), returning line-numbered matches.
    /// </summary>
    private static List<(int Line, string Text)> FindNewButtonExpressions(string filePath)
    {
        var results = new List<(int, string)>();
        var lines = File.ReadAllLines(filePath);
        // Match "new Button" or "new Button(" — the space prevents matching
        // "new ButtonStyle" or similar.
        var regex = new Regex(@"new\s+Button(?:\s*\(|\s*\{|\s*;|$)", RegexOptions.IgnoreCase);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (regex.IsMatch(line))
                results.Add((i + 1, line.Trim()));
        }

        return results;
    }

    // ── XAML scan ────────────────────────────────────────────────────────

    /// <summary>
    /// No raw <c>&lt;Button</c> opening tags may appear in .axaml files
    /// outside the <c>Views/Controls/Buttons/</c> directory (or App.axaml).
    /// </summary>
    [Fact]
    public void NoRawButtonTags_InAxaml_OutsideButtonsDirectory()
    {
        var violations = new List<string>();
        var files = GetAxamlFiles();

        Assert.True(files.Count > 0,
            $"No .axaml files found under {ViewsDir}. "
            + "The Views directory must exist and contain XAML files.");

        foreach (var file in files)
        {
            // Allowed: files inside the central Buttons directory.
            if (IsInButtonsDirectory(file))
                continue;

            // Allowed: App.axaml (may reference Button in style includes).
            if (Path.GetFullPath(file) == Path.GetFullPath(AppAxamlPath))
                continue;

            var matches = FindRawButtonTags(file);
            if (matches.Count > 0)
            {
                var relativePath = Path.GetRelativePath(RepoRoot, file);
                foreach (var (line, text) in matches)
                    violations.Add($"  {relativePath}:{line}  →  {text}");
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} raw <Button> tag(s) in .axaml files "
            + $"outside Views/Controls/Buttons/.  Every button must use a "
            + $"centralized button component (CommonButton, IconButton, "
            + $"StageCardButton, etc.).\n\n"
            + $"Violations:\n{string.Join("\n", violations)}");
    }

    // ── Code-behind scan ─────────────────────────────────────────────────

    /// <summary>
    /// No raw <c>new Button</c> expressions may appear in .cs code-behind
    /// files outside the <c>Views/Controls/Buttons/</c> directory (or
    /// App.axaml.cs for the confirmation dialog).
    /// </summary>
    [Fact]
    public void NoNewButtonExpressions_InCs_OutsideButtonsDirectory()
    {
        var violations = new List<string>();
        var files = GetCsFiles();

        Assert.True(files.Count > 0,
            $"No .cs files found under {ViewsDir}. "
            + "The Views directory must exist and contain code-behind files.");

        foreach (var file in files)
        {
            // Allowed: files inside the central Buttons directory.
            if (IsInButtonsDirectory(file))
                continue;

            // Allowed: App.axaml.cs (confirmation-dialog factory + Cancel).
            if (Path.GetFullPath(file) == Path.GetFullPath(AppAxamlCsPath))
                continue;

            var matches = FindNewButtonExpressions(file);
            if (matches.Count > 0)
            {
                var relativePath = Path.GetRelativePath(RepoRoot, file);
                foreach (var (line, text) in matches)
                    violations.Add($"  {relativePath}:{line}  →  {text}");
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} new Button expression(s) in .cs files "
            + $"outside Views/Controls/Buttons/.  Every button must use a "
            + $"centralized button component (CommonButton, IconButton, "
            + $"StageCardButton, etc.).\n\n"
            + $"Violations:\n{string.Join("\n", violations)}");
    }

    // ── Central directory existence ──────────────────────────────────────

    /// <summary>
    /// The central Buttons directory must exist as the single home for all
    /// custom button components.
    /// </summary>
    [Fact]
    public void ButtonsDirectory_Exists()
    {
        Assert.True(Directory.Exists(ButtonsDir),
            $"The central Buttons directory must exist at {ButtonsDir}.  "
            + "Create it and place CommonButton, IconButton, StageCardButton, "
            + "and any future custom button components there.");
    }
}
